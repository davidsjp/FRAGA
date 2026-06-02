namespace FRAGA.Models;

internal class FreteRegra
{
    public string CepOrigem { get; set; } = string.Empty;
    public string CepDestinoInicial { get; set; } = string.Empty;
    public string CepDestinoFinal { get; set; } = string.Empty;
    public string CodigoTransportadora { get; set; } = string.Empty;
    public string NomeTransportadora { get; set; } = string.Empty;
    public string CnpjTransportadora { get; set; } = string.Empty;
    public string CodigoTabelaPreco { get; set; } = string.Empty;
    public decimal KgMaximo { get; set; }
    public decimal ValorMaximoNf { get; set; }
    public decimal PrecoCorrigido { get; set; }
    public decimal FreteMinimoCorrigido { get; set; }
    public decimal PercentualSobreTotalNf { get; set; }
    public decimal OutraTaxaPercentualNf { get; set; }
    public DateTime? Validade { get; set; }
    public int? PrazoDias { get; set; }
}
