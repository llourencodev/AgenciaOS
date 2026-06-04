namespace AgenciaOS.Models;

public class Notificacao
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public string Mensagem { get; set; } = "";
    public TipoNotificacao Tipo { get; set; }
    public string? Url { get; set; }
    public bool Lida { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public string UsuarioId { get; set; } = "";
    public Usuario Usuario { get; set; } = null!;

    public string IconeCss => Tipo switch
    {
        TipoNotificacao.AprovacaoPendente => "bi-clock-history text-warning",
        TipoNotificacao.TarefaAtrasada => "bi-exclamation-triangle text-danger",
        TipoNotificacao.NovaTarefa => "bi-check2-square text-primary",
        TipoNotificacao.MaterialEnviado => "bi-upload text-info",
        TipoNotificacao.AprovacaoConcluida => "bi-check-circle text-success",
        _ => "bi-bell text-secondary"
    };
}

public enum TipoNotificacao
{
    AprovacaoPendente,
    TarefaAtrasada,
    NovaTarefa,
    MaterialEnviado,
    AprovacaoConcluida,
    Geral
}
