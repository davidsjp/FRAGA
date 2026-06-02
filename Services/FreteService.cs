using System.Data;
using System.Text.RegularExpressions;
using FRAGA.DTOs;
using FRAGA.Models;
using Sap.Data.Hana;

namespace FRAGA.Services;

public class FreteService
{
    private readonly IConfiguration _configuration;

    public FreteService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<FreteCotacaoResponse> CotarAsync(FreteCotacaoRequest request, CancellationToken cancellationToken)
    {
        var entrada = CriarEntrada(request);
        var alertas = ValidarEntrada(entrada);
        if (alertas.Count > 0)
            return Falha("Dados invalidos para cotacao.", entrada, alertas);

        if (!string.IsNullOrWhiteSpace(entrada.CepOrigem) && entrada.CepOrigem == entrada.CepDestino)
        {
            return Falha(
                "CEP de origem e destino sao iguais.",
                entrada,
                new List<FreteAlertaDto>
                {
                    new() { Codigo = "ORIGEM_DESTINO_IGUAIS", Mensagem = "Origem e destino iguais; nao foi gerada cotacao de transporte." }
                });
        }

        var alertasResultado = new List<FreteAlertaDto>();
        var regras = await BuscarRegrasAsync(entrada.CepOrigem, entrada.CepDestino, exigirOrigem: true, cancellationToken);
        if (regras.Count == 0 && !string.IsNullOrWhiteSpace(entrada.CepOrigem))
        {
            regras = await BuscarRegrasAsync(entrada.CepOrigem, entrada.CepDestino, exigirOrigem: false, cancellationToken);
            if (regras.Count > 0)
            {
                alertasResultado.Add(new FreteAlertaDto
                {
                    Codigo = "ROTA_ORIGEM_NAO_CADASTRADA",
                    Mensagem = "Nao existe tabela para o CEP de origem informado; cotacao calculada por faixa de destino."
                });
            }
        }

        if (regras.Count == 0)
        {
            var alerta = string.IsNullOrWhiteSpace(entrada.CepOrigem)
                ? new FreteAlertaDto { Codigo = "SEM_REGRA_CEP", Mensagem = "Nao foi encontrada faixa de frete para o CEP destino." }
                : new FreteAlertaDto { Codigo = "SEM_REGRA_ORIGEM_DESTINO", Mensagem = "Nao foi encontrada tabela de frete para a combinacao de CEP origem e destino." };

            return Falha(
                "Nenhuma regra de frete encontrada para o CEP informado.",
                entrada,
                new List<FreteAlertaDto> { alerta });
        }

        var regrasValidas = regras
            .Where(x => x.KgMaximo <= 0 || entrada.PesoConsideradoKg <= x.KgMaximo)
            .Where(x => x.ValorMaximoNf <= 0 || entrada.ValorNota <= x.ValorMaximoNf)
            .ToList();

        if (regrasValidas.Count == 0)
        {
            return Falha(
                "Existem regras para o CEP, mas nenhuma atende o peso ou valor da nota.",
                entrada,
                new List<FreteAlertaDto>
                {
                    new() { Codigo = "SEM_REGRA_PESO_VALOR", Mensagem = "Peso ou valor da nota acima das faixas encontradas." }
                });
        }

        var opcoes = regrasValidas
            .GroupBy(x => string.Join("|",
                x.CodigoTransportadora,
                x.CodigoTabelaPreco,
                x.KgMaximo,
                x.ValorMaximoNf,
                x.PrecoCorrigido,
                x.FreteMinimoCorrigido,
                x.PercentualSobreTotalNf,
                x.OutraTaxaPercentualNf,
                x.PrazoDias))
            .Select(x => x.First())
            .Select(x => MontarOpcao(x, entrada))
            .OrderBy(x => x.ValorFrete)
            .ThenBy(x => x.PesoMaximoKg)
            .ThenBy(x => x.CodigoTransportadora)
            .ToList();

        opcoes[0].MelhorOpcao = true;

        return new FreteCotacaoResponse
        {
            Sucesso = true,
            Mensagem = "Frete calculado com sucesso.",
            Entrada = entrada,
            MelhorOpcao = opcoes[0],
            Opcoes = opcoes,
            Alertas = alertasResultado
        };
    }

    public async Task<FreteRegrasResponse> ConsultarRegrasAsync(string cepDestino, CancellationToken cancellationToken)
    {
        var cepConsultado = NormalizarCep(cepDestino);
        var itens = new List<FreteRegraItemDto>();

        if (cepConsultado.Length != 8)
            return new FreteRegrasResponse { CepConsultado = cepConsultado };

        var connectionString = ObterConnectionString();

        await using var connection = new HanaConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT DISTINCT
                ? AS ""Cep"",
                PESO.""cep_origem"" AS ""CepOrigem"",
                IFNULL(CEP.""uf"", '') AS ""UF"",
                IFNULL(CEP.""cidade"", '') AS ""Cidade"",
                PESO.""cod_transportadora"" AS ""CodTransportadora"",
                IFNULL(BP.""NomeTransportadora"", '') AS ""NomeTransportadora"",
                IFNULL(TRANS.""cnpj"", '') AS ""CnpjTransportadora"",
                TO_DECIMAL(IFNULL(PESO.""kg_maximo"", 0), 19, 4) AS ""KgMaximo"",
                TO_DECIMAL(IFNULL(PESO.""valor_maximo_nf"", 0), 19, 4) AS ""ValorMaximoNF"",
                PESO.""cod_tabela_preco"" AS ""CodTabelaPreco"",
                TO_DECIMAL(IFNULL(PRECO.""preco"", 0), 19, 4) / 100 AS ""PrecoCorrigido"",
                TO_DECIMAL(IFNULL(PRECO.""frete_minimo"", 0), 19, 4) / 100 AS ""FreteMinimoCorrigido"",
                COALESCE(NULLIF(PESO.""observacao_tabela"", ''), NULLIF(PRECO.""observações"", ''), '') AS ""ObservacaoPrazo"",
                (
                    SELECT COALESCE(
                        MIN(CASE
                            WHEN NULLIF(TRIM(TO_NVARCHAR(P.""cod_transportadora"")), '') IS NOT NULL
                              OR NULLIF(TRIM(TO_NVARCHAR(P.""cnpj"")), '') IS NOT NULL
                            THEN P.""prazo_dias""
                        END),
                        MIN(P.""prazo_dias"")
                    )
                    FROM ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_PRAZO"" P
                    WHERE IFNULL(P.""ativo"", 'Y') = 'Y'
                      AND (NULLIF(TRIM(TO_NVARCHAR(P.""cod_transportadora"")), '') IS NULL
                           OR LPAD(TRIM(TO_NVARCHAR(P.""cod_transportadora"")), 6, '0') = LPAD(TRIM(TO_NVARCHAR(PESO.""cod_transportadora"")), 6, '0'))
                      AND (NULLIF(TRIM(TO_NVARCHAR(P.""cnpj"")), '') IS NULL
                           OR REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cnpj""), '.', ''), '/', ''), '-', ''), ' ', '')
                            = REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(TRANS.""cnpj""), '.', ''), '/', ''), '-', ''), ' ', ''))
                      AND (
                          (
                              NULLIF(TRIM(TO_NVARCHAR(P.""cep_inicial"")), '') IS NOT NULL
                              AND NULLIF(TRIM(TO_NVARCHAR(P.""cep_final"")), '') IS NOT NULL
                              AND TO_BIGINT(?) BETWEEN
                                 LEAST(
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_final""), '-', ''), '.', ''), ' ', ''), ''))
                                 )
                                 AND
                                 GREATEST(
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_final""), '-', ''), '.', ''), ' ', ''), ''))
                                 )
                          )
                          OR (
                              NULLIF(TRIM(TO_NVARCHAR(P.""cep_inicial"")), '') IS NULL
                              AND NULLIF(TRIM(TO_NVARCHAR(P.""cep_final"")), '') IS NULL
                              AND (NULLIF(TRIM(TO_NVARCHAR(P.""uf"")), '') IS NULL OR UPPER(TRIM(TO_NVARCHAR(P.""uf""))) = UPPER(TRIM(TO_NVARCHAR(CEP.""uf""))))
                              AND (NULLIF(TRIM(TO_NVARCHAR(P.""cidade"")), '') IS NULL OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(P.""cidade""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C') = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(CEP.""cidade""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C'))
                          )
                      )
                      AND (
                          NULLIF(TRIM(TO_NVARCHAR(P.""cep_origem_inicial"")), '') IS NULL
                          OR NULLIF(TRIM(TO_NVARCHAR(P.""cep_origem_final"")), '') IS NULL
                          OR TO_BIGINT(NULLIF(?, '')) BETWEEN
                             LEAST(
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_final""), '-', ''), '.', ''), ' ', ''), ''))
                             )
                             AND
                             GREATEST(
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_final""), '-', ''), '.', ''), ' ', ''), ''))
                             )
                      )
                ) AS ""PrazoDiasTabela"",
                (
                    SELECT MIN(NULLIF(CITIES.""deadline"", 0))
                    FROM ""SBO_ELETROPAR_PRD"".""ArkabWebAPIFreightRegionCities"" CITIES
                    WHERE UPPER(TRIM(TO_NVARCHAR(CITIES.""state""))) = UPPER(TRIM(TO_NVARCHAR(CEP.""uf"")))
                      AND (
                          REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(CITIES.""cityname""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C')
                          =
                          REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(CEP.""cidade""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C')
                      )
                      AND (
                          NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_ini""), '-', ''), '.', ''), ' ', ''), '') IS NULL
                          OR NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_fim""), '-', ''), '.', ''), ' ', ''), '') IS NULL
                          OR TO_BIGINT(?) BETWEEN
                              LEAST(TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_ini""), '-', ''), '.', ''), ' ', ''), '')), TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_fim""), '-', ''), '.', ''), ' ', ''), '')))
                              AND
                              GREATEST(TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_ini""), '-', ''), '.', ''), ' ', ''), '')), TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_fim""), '-', ''), '.', ''), ' ', ''), '')))
                      )
                ) AS ""PrazoDiasArkab"",
                CASE
                    WHEN LENGTH(TRIM(TO_NVARCHAR(PRECO.""validade""))) = 7
                        THEN ADD_DAYS(
                            TO_DATE(SUBSTRING(TRIM(TO_NVARCHAR(PRECO.""validade"")), 1, 4) || '-01-01', 'YYYY-MM-DD'),
                            TO_INTEGER(SUBSTRING(TRIM(TO_NVARCHAR(PRECO.""validade"")), 5, 3)) - 1
                        )
                    ELSE NULL
                END AS ""Validade"",
                'OK' AS ""StatusRegra"",
                CASE
                    WHEN PESO.""cod_tabela_preco"" = '900' THEN 'REGRA_ESPECIAL'
                    ELSE 'REGRA_NORMAL'
                END AS ""TipoRegra""
            FROM ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_PESO"" PESO
            INNER JOIN ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_TRANSPORTADORA"" TRANS
                ON LPAD(TRIM(TO_NVARCHAR(TRANS.""cod_transportadora"")), 6, '0')
                 = LPAD(TRIM(TO_NVARCHAR(PESO.""cod_transportadora"")), 6, '0')
            INNER JOIN ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_PRECO"" PRECO
                ON LPAD(TRIM(TO_NVARCHAR(PRECO.""cod_transportadora"")), 6, '0')
                 = LPAD(TRIM(TO_NVARCHAR(PESO.""cod_transportadora"")), 6, '0')
               AND PRECO.""numero_da_tabela"" = PESO.""cod_tabela_preco""
            LEFT JOIN ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_CEP"" CEP
                ON REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CEP.""cep""), '-', ''), '.', ''), ' ', '') = ?
            LEFT JOIN (
                SELECT
                    REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(D.""TaxId0""), '.', ''), '/', ''), '-', ''), ' ', '') AS ""CnpjNormalizado"",
                    MAX(C.""CardName"") AS ""NomeTransportadora""
                FROM ""SBO_ELETROPAR_PRD"".""CRD7"" D
                INNER JOIN ""SBO_ELETROPAR_PRD"".""OCRD"" C
                    ON C.""CardCode"" = D.""CardCode""
                WHERE IFNULL(D.""TaxId0"", '') <> ''
                GROUP BY REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(D.""TaxId0""), '.', ''), '/', ''), '-', ''), ' ', '')
            ) BP
                ON BP.""CnpjNormalizado"" = REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(TRANS.""cnpj""), '.', ''), '/', ''), '-', ''), ' ', '')
            WHERE TO_BIGINT(?) BETWEEN
                LEAST(
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_final""), '-', ''), '.', ''), ' ', ''), ''))
                )
                AND
                GREATEST(
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_final""), '-', ''), '.', ''), ' ', ''), ''))
                )
              AND TO_DECIMAL(IFNULL(PRECO.""preco"", 0), 19, 4) / 100 >= 5
            ORDER BY ""CodTransportadora"", ""KgMaximo"", ""ValorMaximoNF"", ""CodTabelaPreco""";
        command.Parameters.Add(new HanaParameter { Value = cepConsultado });
        command.Parameters.Add(new HanaParameter { Value = cepConsultado });
        command.Parameters.Add(new HanaParameter { Value = string.Empty });
        command.Parameters.Add(new HanaParameter { Value = cepConsultado });
        command.Parameters.Add(new HanaParameter { Value = cepConsultado });
        command.Parameters.Add(new HanaParameter { Value = cepConsultado });

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            itens.Add(new FreteRegraItemDto
            {
                Cep = GetString(reader, "Cep"),
                CepOrigem = GetString(reader, "CepOrigem"),
                Uf = GetString(reader, "UF"),
                Cidade = GetString(reader, "Cidade"),
                CodTransportadora = GetString(reader, "CodTransportadora"),
                NomeTransportadora = GetString(reader, "NomeTransportadora"),
                CnpjTransportadora = GetString(reader, "CnpjTransportadora"),
                KgMaximo = Round(GetDecimal(reader, "KgMaximo")),
                ValorMaximoNF = Round(GetDecimal(reader, "ValorMaximoNF")),
                CodTabelaPreco = GetString(reader, "CodTabelaPreco"),
                PrecoCorrigido = Round(GetDecimal(reader, "PrecoCorrigido")),
                FreteMinimoCorrigido = Round(GetDecimal(reader, "FreteMinimoCorrigido")),
                Validade = GetDateOnly(reader, "Validade"),
                PrazoDias = GetIntNullable(reader, "PrazoDiasTabela")
                    ?? ParsePrazoDias(GetString(reader, "ObservacaoPrazo"))
                    ?? GetIntNullable(reader, "PrazoDiasArkab"),
                StatusRegra = GetString(reader, "StatusRegra"),
                TipoRegra = GetString(reader, "TipoRegra")
            });
        }

        return new FreteRegrasResponse
        {
            CepConsultado = cepConsultado,
            Total = itens.Count,
            Itens = itens
        };
    }

    private async Task<List<FreteRegra>> BuscarRegrasAsync(string cepOrigem, string cepDestino, bool exigirOrigem, CancellationToken cancellationToken)
    {
        var connectionString = ObterConnectionString();

        await using var connection = new HanaConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT
                PESO.""cep_origem"" AS ""CepOrigem"",
                PESO.""cep_destino_inicial"" AS ""CepDestinoInicial"",
                PESO.""cep_destino_final"" AS ""CepDestinoFinal"",
                PESO.""cod_transportadora"" AS ""CodTransportadora"",
                IFNULL(BP.""NomeTransportadora"", '') AS ""NomeTransportadora"",
                IFNULL(TRANS.""cnpj"", '') AS ""CnpjTransportadora"",
                PESO.""cod_tabela_preco"" AS ""CodTabelaPreco"",
                IFNULL(CEP.""uf"", '') AS ""UF"",
                IFNULL(CEP.""cidade"", '') AS ""Cidade"",
                TO_DECIMAL(IFNULL(PESO.""kg_maximo"", 0), 19, 4) AS ""KgMaximo"",
                TO_DECIMAL(IFNULL(PESO.""valor_maximo_nf"", 0), 19, 4) AS ""ValorMaximoNF"",
                TO_DECIMAL(IFNULL(PRECO.""preco"", 0), 19, 4) / 100 AS ""PrecoCorrigido"",
                TO_DECIMAL(IFNULL(PRECO.""frete_minimo"", 0), 19, 4) / 100 AS ""FreteMinimoCorrigido"",
                TO_DECIMAL(IFNULL(PRECO.""adicional_por_kg"", 0), 19, 4) / 100 AS ""AdicionalPorKg"",
                TO_DECIMAL(IFNULL(PRECO.""fracao_kg"", 0), 19, 4) AS ""FracaoKg"",
                TO_DECIMAL(IFNULL(PRECO.""perc_sobre_total_nf"", 0), 19, 4) AS ""PercentualSobreTotalNF"",
                TO_DECIMAL(IFNULL(PRECO.""outra_taxa_5_perc_nf"", 0), 19, 4) AS ""OutraTaxaPercentualNF"",
                IFNULL(PRECO.""aplica_fracao_kg"", 'N') AS ""AplicaFracaoKg"",
                IFNULL(PRECO.""despreza_kg_fracao_kg"", 'N') AS ""DesprezaKgFracaoKg"",
                (
                    SELECT COALESCE(
                        MIN(CASE
                            WHEN NULLIF(TRIM(TO_NVARCHAR(P.""cod_transportadora"")), '') IS NOT NULL
                              OR NULLIF(TRIM(TO_NVARCHAR(P.""cnpj"")), '') IS NOT NULL
                            THEN P.""prazo_dias""
                        END),
                        MIN(P.""prazo_dias"")
                    )
                    FROM ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_PRAZO"" P
                    WHERE IFNULL(P.""ativo"", 'Y') = 'Y'
                      AND (NULLIF(TRIM(TO_NVARCHAR(P.""cod_transportadora"")), '') IS NULL
                           OR LPAD(TRIM(TO_NVARCHAR(P.""cod_transportadora"")), 6, '0') = LPAD(TRIM(TO_NVARCHAR(PESO.""cod_transportadora"")), 6, '0'))
                      AND (NULLIF(TRIM(TO_NVARCHAR(P.""cnpj"")), '') IS NULL
                           OR REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cnpj""), '.', ''), '/', ''), '-', ''), ' ', '')
                            = REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(TRANS.""cnpj""), '.', ''), '/', ''), '-', ''), ' ', ''))
                      AND (
                          (
                              NULLIF(TRIM(TO_NVARCHAR(P.""cep_inicial"")), '') IS NOT NULL
                              AND NULLIF(TRIM(TO_NVARCHAR(P.""cep_final"")), '') IS NOT NULL
                              AND TO_BIGINT(?) BETWEEN
                                 LEAST(
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_final""), '-', ''), '.', ''), ' ', ''), ''))
                                 )
                                 AND
                                 GREATEST(
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                     TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_final""), '-', ''), '.', ''), ' ', ''), ''))
                                 )
                          )
                          OR (
                              NULLIF(TRIM(TO_NVARCHAR(P.""cep_inicial"")), '') IS NULL
                              AND NULLIF(TRIM(TO_NVARCHAR(P.""cep_final"")), '') IS NULL
                              AND (NULLIF(TRIM(TO_NVARCHAR(P.""uf"")), '') IS NULL OR UPPER(TRIM(TO_NVARCHAR(P.""uf""))) = UPPER(TRIM(TO_NVARCHAR(CEP.""uf""))))
                              AND (NULLIF(TRIM(TO_NVARCHAR(P.""cidade"")), '') IS NULL OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(P.""cidade""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C') = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(CEP.""cidade""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C'))
                          )
                      )
                      AND (
                          NULLIF(TRIM(TO_NVARCHAR(P.""cep_origem_inicial"")), '') IS NULL
                          OR NULLIF(TRIM(TO_NVARCHAR(P.""cep_origem_final"")), '') IS NULL
                          OR TO_BIGINT(NULLIF(?, '')) BETWEEN
                             LEAST(
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_final""), '-', ''), '.', ''), ' ', ''), ''))
                             )
                             AND
                             GREATEST(
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                                 TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(P.""cep_origem_final""), '-', ''), '.', ''), ' ', ''), ''))
                             )
                      )
                ) AS ""PrazoDiasTabela"",
                (
                    SELECT MIN(NULLIF(CITIES.""deadline"", 0))
                    FROM ""SBO_ELETROPAR_PRD"".""ArkabWebAPIFreightRegionCities"" CITIES
                    WHERE UPPER(TRIM(TO_NVARCHAR(CITIES.""state""))) = UPPER(TRIM(TO_NVARCHAR(CEP.""uf"")))
                      AND (
                          REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(CITIES.""cityname""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C')
                          =
                          REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(TRIM(TO_NVARCHAR(CEP.""cidade""))), 'Á', 'A'), 'À', 'A'), 'Â', 'A'), 'Ã', 'A'), 'É', 'E'), 'Ê', 'E'), 'Í', 'I'), 'Ó', 'O'), 'Ô', 'O'), 'Õ', 'O'), 'Ç', 'C')
                      )
                      AND (
                          NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_ini""), '-', ''), '.', ''), ' ', ''), '') IS NULL
                          OR NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_fim""), '-', ''), '.', ''), ' ', ''), '') IS NULL
                          OR TO_BIGINT(?) BETWEEN
                              LEAST(TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_ini""), '-', ''), '.', ''), ' ', ''), '')), TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_fim""), '-', ''), '.', ''), ' ', ''), '')))
                              AND
                              GREATEST(TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_ini""), '-', ''), '.', ''), ' ', ''), '')), TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CITIES.""cep_fim""), '-', ''), '.', ''), ' ', ''), '')))
                      )
                ) AS ""PrazoDiasArkab"",
                COALESCE(NULLIF(PESO.""observacao_tabela"", ''), NULLIF(PRECO.""observações"", ''), '') AS ""ObservacaoPrazo"",
                CASE
                    WHEN LENGTH(TRIM(TO_NVARCHAR(PRECO.""validade""))) = 7
                        THEN ADD_DAYS(
                            TO_DATE(SUBSTRING(TRIM(TO_NVARCHAR(PRECO.""validade"")), 1, 4) || '-01-01', 'YYYY-MM-DD'),
                            TO_INTEGER(SUBSTRING(TRIM(TO_NVARCHAR(PRECO.""validade"")), 5, 3)) - 1
                        )
                    ELSE NULL
                END AS ""Validade""
            FROM ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_PESO"" PESO
            INNER JOIN ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_TRANSPORTADORA"" TRANS
                ON LPAD(TRIM(TO_NVARCHAR(TRANS.""cod_transportadora"")), 6, '0')
                 = LPAD(TRIM(TO_NVARCHAR(PESO.""cod_transportadora"")), 6, '0')
            INNER JOIN ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_PRECO"" PRECO
                ON LPAD(TRIM(TO_NVARCHAR(PRECO.""cod_transportadora"")), 6, '0')
                 = LPAD(TRIM(TO_NVARCHAR(PESO.""cod_transportadora"")), 6, '0')
               AND PRECO.""numero_da_tabela"" = PESO.""cod_tabela_preco""
            LEFT JOIN ""SBO_ELETROPAR_PRD"".""FRETE_ELETROPAR_CEP"" CEP
                ON REPLACE(REPLACE(REPLACE(TO_NVARCHAR(CEP.""cep""), '-', ''), '.', ''), ' ', '') = ?
            LEFT JOIN (
                SELECT
                    REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(D.""TaxId0""), '.', ''), '/', ''), '-', ''), ' ', '') AS ""CnpjNormalizado"",
                    MAX(C.""CardName"") AS ""NomeTransportadora""
                FROM ""SBO_ELETROPAR_PRD"".""CRD7"" D
                INNER JOIN ""SBO_ELETROPAR_PRD"".""OCRD"" C
                    ON C.""CardCode"" = D.""CardCode""
                WHERE IFNULL(D.""TaxId0"", '') <> ''
                GROUP BY REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(D.""TaxId0""), '.', ''), '/', ''), '-', ''), ' ', '')
            ) BP
                ON BP.""CnpjNormalizado"" = REPLACE(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(TRANS.""cnpj""), '.', ''), '/', ''), '-', ''), ' ', '')
            WHERE TO_BIGINT(?) BETWEEN
                LEAST(
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_final""), '-', ''), '.', ''), ' ', ''), ''))
                )
                AND
                GREATEST(
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_inicial""), '-', ''), '.', ''), ' ', ''), '')),
                    TO_BIGINT(NULLIF(REPLACE(REPLACE(REPLACE(TO_NVARCHAR(PESO.""cep_destino_final""), '-', ''), '.', ''), ' ', ''), ''))
                )
              AND TO_DECIMAL(IFNULL(PRECO.""preco"", 0), 19, 4) / 100 >= 5
            ORDER BY ""CodTransportadora"", ""KgMaximo"", ""ValorMaximoNF""";
        command.Parameters.Add(new HanaParameter { Value = cepDestino });
        command.Parameters.Add(new HanaParameter { Value = cepOrigem });
        command.Parameters.Add(new HanaParameter { Value = cepDestino });
        command.Parameters.Add(new HanaParameter { Value = cepDestino });
        command.Parameters.Add(new HanaParameter { Value = cepDestino });

        var regras = new List<FreteRegra>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            regras.Add(new FreteRegra
            {
                CepOrigem = GetString(reader, "CepOrigem"),
                CepDestinoInicial = GetString(reader, "CepDestinoInicial"),
                CepDestinoFinal = GetString(reader, "CepDestinoFinal"),
                CodigoTransportadora = GetString(reader, "CodTransportadora"),
                NomeTransportadora = GetString(reader, "NomeTransportadora"),
                CnpjTransportadora = GetString(reader, "CnpjTransportadora"),
                CodigoTabelaPreco = GetString(reader, "CodTabelaPreco"),
                KgMaximo = GetDecimal(reader, "KgMaximo"),
                ValorMaximoNf = GetDecimal(reader, "ValorMaximoNF"),
                PrecoCorrigido = GetDecimal(reader, "PrecoCorrigido"),
                FreteMinimoCorrigido = GetDecimal(reader, "FreteMinimoCorrigido"),
                AdicionalPorKg = GetDecimal(reader, "AdicionalPorKg"),
                FracaoKg = GetDecimal(reader, "FracaoKg"),
                PercentualSobreTotalNf = GetDecimal(reader, "PercentualSobreTotalNF"),
                OutraTaxaPercentualNf = GetDecimal(reader, "OutraTaxaPercentualNF"),
                AplicaFracaoKg = GetBoolFlag(reader, "AplicaFracaoKg"),
                DesprezaKgFracaoKg = GetBoolFlag(reader, "DesprezaKgFracaoKg"),
                Validade = GetDate(reader, "Validade"),
                PrazoDias = GetIntNullable(reader, "PrazoDiasTabela")
                    ?? ParsePrazoDias(GetString(reader, "ObservacaoPrazo"))
                    ?? GetIntNullable(reader, "PrazoDiasArkab")
            });
        }

        if (exigirOrigem && !string.IsNullOrWhiteSpace(cepOrigem))
            regras = regras.Where(x => OrigemCompativel(cepOrigem, x.CepOrigem)).ToList();

        return regras;
    }

    private string ObterConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("HanaConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = Environment.GetEnvironmentVariable("FRAGA_HANA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Connection string HanaConnection nao configurada.");

        return connectionString;
    }

    private static FreteOpcaoDto MontarOpcao(FreteRegra regra, FreteEntradaDto entrada)
    {
        var baseFrete = Math.Max(regra.PrecoCorrigido + CalcularAdicionalPeso(regra, entrada.PesoConsideradoKg), regra.FreteMinimoCorrigido);
        var valorAdValorem = entrada.ValorBaseAdValorem * regra.PercentualSobreTotalNf / 100m;
        var valorOutrasTaxas = entrada.ValorBaseAdValorem * regra.OutraTaxaPercentualNf / 100m;
        var usaPercentualSobrePedido = regra.PercentualSobreTotalNf > 0 || regra.OutraTaxaPercentualNf > 0;
        var valorFrete = usaPercentualSobrePedido
            ? Math.Max(valorAdValorem + valorOutrasTaxas, regra.FreteMinimoCorrigido)
            : baseFrete;

        return new FreteOpcaoDto
        {
            CodigoTransportadora = regra.CodigoTransportadora,
            NomeTransportadora = regra.NomeTransportadora,
            CnpjTransportadora = regra.CnpjTransportadora,
            CodigoTabelaPreco = regra.CodigoTabelaPreco,
            ValorFrete = Round(valorFrete),
            FreteMinimo = Round(regra.FreteMinimoCorrigido),
            ValorAdValorem = Round(valorAdValorem),
            PercentualAdValorem = Round(regra.PercentualSobreTotalNf, 4),
            ValorOutrasTaxas = Round(valorOutrasTaxas),
            PercentualOutrasTaxas = Round(regra.OutraTaxaPercentualNf, 4),
            PesoMaximoKg = Round(regra.KgMaximo),
            ValorMaximoNota = Round(regra.ValorMaximoNf),
            Validade = regra.Validade,
            PrazoDias = regra.PrazoDias
        };
    }

    private static FreteEntradaDto CriarEntrada(FreteCotacaoRequest request)
    {
        var fatorCubagem = request.FatorCubagem <= 0 ? 1m : request.FatorCubagem;
        var pesoCubado = 0m;
        var pesoConsiderado = request.PesoKg;
        var valorBaseAdValorem = request.SubtotalLiquido
            ?? request.ValorBaseAdValorem
            ?? request.ValorNota;

        return new FreteEntradaDto
        {
            CepOrigem = NormalizarCep(request.CepOrigem),
            CepDestino = NormalizarCep(request.CepDestino),
            PesoKg = Round(request.PesoKg),
            VolumeM3 = Round(request.VolumeM3, 6),
            FatorCubagem = Round(fatorCubagem),
            PesoCubadoKg = Round(pesoCubado),
            PesoConsideradoKg = Round(pesoConsiderado),
            ValorNota = Round(request.ValorNota),
            ValorBaseAdValorem = Round(valorBaseAdValorem),
            QuantidadeVolumes = request.QuantidadeVolumes <= 0 ? 1 : request.QuantidadeVolumes
        };
    }

    private static List<FreteAlertaDto> ValidarEntrada(FreteEntradaDto entrada)
    {
        var alertas = new List<FreteAlertaDto>();

        if (string.IsNullOrWhiteSpace(entrada.CepDestino))
            alertas.Add(new FreteAlertaDto { Codigo = "CEP_DESTINO_OBRIGATORIO", Mensagem = "CEP destino obrigatorio." });

        if (entrada.CepDestino.Length is > 0 and < 8)
            alertas.Add(new FreteAlertaDto { Codigo = "CEP_DESTINO_INVALIDO", Mensagem = "CEP destino deve conter 8 digitos." });

        if (entrada.PesoKg <= 0)
            alertas.Add(new FreteAlertaDto { Codigo = "PESO_OBRIGATORIO", Mensagem = "Informe peso para calcular o frete." });

        if (entrada.ValorNota < 0)
            alertas.Add(new FreteAlertaDto { Codigo = "VALOR_NOTA_INVALIDO", Mensagem = "Valor da nota nao pode ser negativo." });

        if (entrada.ValorBaseAdValorem < 0)
            alertas.Add(new FreteAlertaDto { Codigo = "VALOR_BASE_AD_VALOREM_INVALIDO", Mensagem = "Valor base do ad valorem nao pode ser negativo." });

        return alertas;
    }

    private static FreteCotacaoResponse Falha(string mensagem, FreteEntradaDto entrada, List<FreteAlertaDto> alertas)
    {
        return new FreteCotacaoResponse
        {
            Sucesso = false,
            Mensagem = mensagem,
            Entrada = entrada,
            Alertas = alertas
        };
    }

    private static string NormalizarCep(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value, @"\D", string.Empty);
    }

    private static bool OrigemCompativel(string cepOrigemInformado, string cepOrigemRegra)
    {
        var origemInformada = NormalizarCep(cepOrigemInformado);
        var origemRegra = NormalizarCep(cepOrigemRegra);
        if (origemInformada.Length != 8 || origemRegra.Length != 8)
            return false;

        if (origemInformada == origemRegra)
            return true;

        var ufInformada = ObterUfPorCep(origemInformada);
        var ufRegra = ObterUfPorCep(origemRegra);

        return !string.IsNullOrWhiteSpace(ufInformada)
            && string.Equals(ufInformada, ufRegra, StringComparison.OrdinalIgnoreCase);
    }

    private static string ObterUfPorCep(string cep)
    {
        if (!int.TryParse(cep[..5], out var prefixo))
            return string.Empty;

        return prefixo switch
        {
            >= 01000 and <= 19999 => "SP",
            >= 20000 and <= 28999 => "RJ",
            >= 29000 and <= 29999 => "ES",
            >= 30000 and <= 39999 => "MG",
            >= 40000 and <= 48999 => "BA",
            >= 49000 and <= 49999 => "SE",
            >= 50000 and <= 56999 => "PE",
            >= 57000 and <= 57999 => "AL",
            >= 58000 and <= 58999 => "PB",
            >= 59000 and <= 59999 => "RN",
            >= 60000 and <= 63999 => "CE",
            >= 64000 and <= 64999 => "PI",
            >= 65000 and <= 65999 => "MA",
            >= 66000 and <= 68899 => "PA",
            >= 68900 and <= 68999 => "AP",
            >= 69000 and <= 69299 => "AM",
            >= 69300 and <= 69399 => "RR",
            >= 69400 and <= 69899 => "AM",
            >= 69900 and <= 69999 => "AC",
            >= 70000 and <= 72799 => "DF",
            >= 72800 and <= 72999 => "GO",
            >= 73000 and <= 73699 => "DF",
            >= 73700 and <= 76799 => "GO",
            >= 76800 and <= 76999 => "RO",
            >= 77000 and <= 77999 => "TO",
            >= 78000 and <= 78899 => "MT",
            >= 79000 and <= 79999 => "MS",
            >= 80000 and <= 87999 => "PR",
            >= 88000 and <= 89999 => "SC",
            >= 90000 and <= 99999 => "RS",
            _ => string.Empty
        };
    }

    private static string GetString(IDataRecord reader, string columnName)
    {
        var value = reader[columnName];
        return value == null || Convert.IsDBNull(value) ? string.Empty : Convert.ToString(value) ?? string.Empty;
    }

    private static decimal GetDecimal(IDataRecord reader, string columnName)
    {
        var value = reader[columnName];
        return value == null || Convert.IsDBNull(value) ? 0 : Convert.ToDecimal(value);
    }

    private static int? GetIntNullable(IDataRecord reader, string columnName)
    {
        var value = reader[columnName];
        return value == null || Convert.IsDBNull(value) ? null : Convert.ToInt32(value);
    }

    private static DateTime? GetDate(IDataRecord reader, string columnName)
    {
        var value = reader[columnName];
        if (value == null || Convert.IsDBNull(value))
            return null;

        return value is DateTime date ? date : Convert.ToDateTime(value);
    }

    private static bool GetBoolFlag(IDataRecord reader, string columnName)
    {
        var value = GetString(reader, columnName).Trim();
        return value.Equals("Y", StringComparison.OrdinalIgnoreCase)
            || value.Equals("S", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal CalcularAdicionalPeso(FreteRegra regra, decimal pesoConsideradoKg)
    {
        if (!regra.AplicaFracaoKg || regra.AdicionalPorKg <= 0 || regra.FracaoKg <= 0)
            return 0;

        var pesoExcedente = Math.Max(0, pesoConsideradoKg - regra.KgMaximo);
        if (pesoExcedente <= 0)
            return 0;

        var fracoes = regra.DesprezaKgFracaoKg
            ? Math.Floor(pesoExcedente / regra.FracaoKg)
            : Math.Ceiling(pesoExcedente / regra.FracaoKg);

        return fracoes * regra.AdicionalPorKg;
    }

    private static DateOnly? GetDateOnly(IDataRecord reader, string columnName)
    {
        var date = GetDate(reader, columnName);
        return date.HasValue ? DateOnly.FromDateTime(date.Value) : null;
    }

    private static int? ParsePrazoDias(string observacao)
    {
        if (string.IsNullOrWhiteSpace(observacao))
            return null;

        var dPlusMatch = Regex.Match(observacao, @"D\s*\+\s*(\d+)", RegexOptions.IgnoreCase);
        if (dPlusMatch.Success && int.TryParse(dPlusMatch.Groups[1].Value, out var dPlusDays))
            return dPlusDays;

        var hourMatches = Regex.Matches(observacao, @"\d+(?=\s*(?:H|HR|HRS|HORA|HORAS)\b)", RegexOptions.IgnoreCase);
        if (hourMatches.Count > 0)
        {
            var maxHours = hourMatches
                .Select(x => int.TryParse(x.Value, out var hours) ? hours : 0)
                .Max();

            if (maxHours > 0)
                return (int)Math.Ceiling(maxHours / 24m);
        }

        var dayMatch = Regex.Match(observacao, @"(\d+)\s*(?:DIA|DIAS)\b", RegexOptions.IgnoreCase);
        if (dayMatch.Success && int.TryParse(dayMatch.Groups[1].Value, out var days))
            return days;

        return null;
    }

    private static decimal Round(decimal value, int places = 2)
    {
        return Math.Round(value, places, MidpointRounding.AwayFromZero);
    }
}
