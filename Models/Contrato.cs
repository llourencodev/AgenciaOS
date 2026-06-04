namespace AgenciaOS.Models;

public class Contrato
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public string Conteudo { get; set; } = "";
    public string? Observacoes { get; set; }
    public StatusContrato Status { get; set; } = StatusContrato.Rascunho;
    public decimal? Valor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? DataAssinatura { get; set; }
    public DateTime? DataVigencia { get; set; }

    public int ClienteId { get; set; }
    public Cliente Cliente { get; set; } = null!;

    public string? CriadoPorId { get; set; }
    public Usuario? CriadoPor { get; set; }
}

public enum StatusContrato { Rascunho, Enviado, Assinado, Cancelado }
