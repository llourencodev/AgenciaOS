namespace AgenciaOS.Models;

public class Tarefa
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descricao { get; set; }
    public PrioridadeTarefa Prioridade { get; set; } = PrioridadeTarefa.Media;
    public StatusTarefa Status { get; set; } = StatusTarefa.Pendente;
    public DateTime? Prazo { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public int? ClienteId { get; set; }
    public Cliente? Cliente { get; set; }

    public string? ResponsavelId { get; set; }
    public Usuario? Responsavel { get; set; }

    public string? CriadoPorId { get; set; }
    public Usuario? CriadoPor { get; set; }

    public bool Atrasada => Prazo.HasValue && Prazo < DateTime.UtcNow && Status != StatusTarefa.Concluido;
}

public enum PrioridadeTarefa { Baixa, Media, Alta, Urgente }
public enum StatusTarefa { Pendente, EmAndamento, AguardandoRevisao, Concluido }
