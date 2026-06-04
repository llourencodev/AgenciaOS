namespace AgenciaOS.Models;

public class Tarefa
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descricao { get; set; }
    public TipoTarefa Tipo { get; set; } = TipoTarefa.Outro;
    public PrioridadeTarefa Prioridade { get; set; } = PrioridadeTarefa.Media;
    public StatusTarefa Status { get; set; } = StatusTarefa.Pendente;
    public DateTime? Prazo { get; set; }
    public bool Recorrente { get; set; }
    public FrequenciaRecorrencia? FrequenciaRecorrencia { get; set; }
    public DateTime? DataFimRecorrencia { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public int? ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public string? ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }

    public string? CriadoPorId { get; set; }
    public Usuario? CriadoPor { get; set; }

    public bool Atrasada => Prazo.HasValue && Prazo < DateTime.UtcNow && Status != StatusTarefa.Concluido;

    public string CorTipo => Tipo switch
    {
        TipoTarefa.Design      => "#8b5cf6",
        TipoTarefa.Edicao      => "#f59e0b",
        TipoTarefa.Redacao     => "#3b82f6",
        TipoTarefa.Reuniao     => "#10b981",
        TipoTarefa.Publicacao  => "#ef4444",
        TipoTarefa.Planejamento => "#6366f1",
        _                      => "#6b7280"
    };

    public string IconeTipo => Tipo switch
    {
        TipoTarefa.Design      => "palette-fill",
        TipoTarefa.Edicao      => "scissors",
        TipoTarefa.Redacao     => "pencil-fill",
        TipoTarefa.Reuniao     => "camera-video-fill",
        TipoTarefa.Publicacao  => "send-fill",
        TipoTarefa.Planejamento => "clipboard2-check-fill",
        _                      => "check2-square"
    };
}

public enum PrioridadeTarefa { Baixa, Media, Alta, Urgente }
public enum StatusTarefa { Pendente, EmAndamento, AguardandoRevisao, Concluido }
public enum TipoTarefa { Design, Edicao, Redacao, Reuniao, Publicacao, Planejamento, Outro }
public enum FrequenciaRecorrencia { Diaria, Semanal, Quinzenal, Mensal }
