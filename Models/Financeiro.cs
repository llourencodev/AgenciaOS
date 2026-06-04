namespace AgenciaOS.Models;

public class Financeiro
{
    public int Id { get; set; }
    public string Descricao { get; set; } = "";
    public decimal Valor { get; set; }
    public TipoFinanceiro Tipo { get; set; }
    public CategoriaFinanceiro Categoria { get; set; }
    public DateTime DataVencimento { get; set; }
    public DateTime? DataPagamento { get; set; }
    public bool Pago { get; set; }
    public string? Observacoes { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public int? ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public string? CriadoPorId { get; set; }
    public Usuario? CriadoPor { get; set; }

    public bool Vencida => !Pago && DataVencimento < DateTime.UtcNow;
}

public enum TipoFinanceiro { Entrada, Saida }
public enum CategoriaFinanceiro
{
    Mensalidade,
    Projeto,
    PagamentoExtra,
    Freelancer,
    Software,
    Anuncio,
    Fornecedor,
    Outros
}
