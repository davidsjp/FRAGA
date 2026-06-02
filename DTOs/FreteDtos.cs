namespace FRAGA.DTOs;

public class FreteCotacaoRequest
{
    public string CepDestino { get; set; } = string.Empty;
    public string? CepOrigem { get; set; }
    public decimal PesoKg { get; set; }
    public decimal ValorNota { get; set; }
    public decimal? SubtotalLiquido { get; set; }
    public decimal? ValorBaseAdValorem { get; set; }
    public decimal VolumeM3 { get; set; }
    public int QuantidadeVolumes { get; set; } = 1;
    public decimal FatorCubagem { get; set; } = 300m;
}

public class FreteCotacaoResponse
{
    public bool Sucesso { get; set; }
    public string Mensagem { get; set; } = string.Empty;
    public FreteEntradaDto Entrada { get; set; } = new();
    public FreteOpcaoDto? MelhorOpcao { get; set; }
    public List<FreteOpcaoDto> Opcoes { get; set; } = new();
    public List<FreteAlertaDto> Alertas { get; set; } = new();
}

public class FreteEntradaDto
{
    public string CepOrigem { get; set; } = string.Empty;
    public string CepDestino { get; set; } = string.Empty;
    public decimal PesoKg { get; set; }
    public decimal VolumeM3 { get; set; }
    public decimal FatorCubagem { get; set; }
    public decimal PesoCubadoKg { get; set; }
    public decimal PesoConsideradoKg { get; set; }
    public decimal ValorNota { get; set; }
    public decimal ValorBaseAdValorem { get; set; }
    public int QuantidadeVolumes { get; set; }
}

public class FreteOpcaoDto
{
    public bool MelhorOpcao { get; set; }
    public string CodigoTransportadora { get; set; } = string.Empty;
    public string NomeTransportadora { get; set; } = string.Empty;
    public string CnpjTransportadora { get; set; } = string.Empty;
    public string CodigoTabelaPreco { get; set; } = string.Empty;
    public decimal ValorFrete { get; set; }
    public decimal FreteMinimo { get; set; }
    public decimal ValorAdValorem { get; set; }
    public decimal PercentualAdValorem { get; set; }
    public decimal ValorOutrasTaxas { get; set; }
    public decimal PercentualOutrasTaxas { get; set; }
    public decimal PesoMaximoKg { get; set; }
    public decimal ValorMaximoNota { get; set; }
    public DateTime? Validade { get; set; }
    public int? PrazoDias { get; set; }
}

public class FreteAlertaDto
{
    public string Codigo { get; set; } = string.Empty;
    public string Mensagem { get; set; } = string.Empty;
}

public class FreteRegrasResponse
{
    public string CepConsultado { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<FreteRegraItemDto> Itens { get; set; } = new();
}

public class FreteRegraItemDto
{
    public string Cep { get; set; } = string.Empty;
    public string CepOrigem { get; set; } = string.Empty;
    public string Uf { get; set; } = string.Empty;
    public string Cidade { get; set; } = string.Empty;
    public string CodTransportadora { get; set; } = string.Empty;
    public string NomeTransportadora { get; set; } = string.Empty;
    public string CnpjTransportadora { get; set; } = string.Empty;
    public decimal KgMaximo { get; set; }
    public decimal ValorMaximoNF { get; set; }
    public string CodTabelaPreco { get; set; } = string.Empty;
    public decimal PrecoCorrigido { get; set; }
    public decimal FreteMinimoCorrigido { get; set; }
    public DateOnly? Validade { get; set; }
    public int? PrazoDias { get; set; }
    public string StatusRegra { get; set; } = string.Empty;
    public string TipoRegra { get; set; } = string.Empty;
}
