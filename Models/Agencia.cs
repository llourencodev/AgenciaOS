namespace AgenciaOS.Models;

/// <summary>
/// Agência assinante (tenant). Todo dado de negócio pertence a exatamente uma agência.
/// Identidade visual (logo, cores) fica em <see cref="Configuracao"/>, uma por agência.
/// </summary>
public class Agencia
{
    public int Id { get; set; }
    public string Nome { get; set; } = "";
    public string? NomeFantasia { get; set; }
    public string Slug { get; set; } = "";
    public string? Cnpj { get; set; }
    public string? EmailPrincipal { get; set; }
    public string? Telefone { get; set; }

    public StatusAgencia Status { get; set; } = StatusAgencia.Ativa;
    public SituacaoAssinatura SituacaoAssinatura { get; set; } = SituacaoAssinatura.Teste;
    public string? PlanoCodigo { get; set; }
    public DateTime? TesteExpiraEm { get; set; }
    /// <summary>Isenta de assinatura (cortesia decidida pelo proprietário): nunca é bloqueada por cobrança e não tem limites de plano.</summary>
    public bool Isenta { get; set; }
    /// <summary>Motivo da suspensão administrativa (Status = Suspensa).</summary>
    public string? MotivoSuspensao { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public string NomeExibicao => string.IsNullOrWhiteSpace(NomeFantasia) ? Nome : NomeFantasia;
}

public enum StatusAgencia { Ativa, Suspensa, Cancelada }

public enum SituacaoAssinatura { Teste, Ativa, PagamentoPendente, Atrasada, Tolerancia, Suspensa, Cancelada }

/// <summary>
/// Entidade que pertence a uma agência. Recebe filtro global de consulta e é verificada
/// pela guarda de escrita do <c>ApplicationDbContext</c>.
/// </summary>
public interface IPertenceAgencia
{
    int Id { get; }
    int AgenciaId { get; set; }
}
