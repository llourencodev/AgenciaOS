namespace AgenciaOS.Models;

public class Cliente
{
    public int Id { get; set; }
    public string NomeEmpresa { get; set; } = "";
    public string Responsavel { get; set; } = "";
    public string? Telefone { get; set; }
    public string Email { get; set; } = "";
    public string? Instagram { get; set; }
    public string? PlanoContratado { get; set; }
    public string? Observacoes { get; set; }
    public string? Estrategia { get; set; }
    public string? Objetivos { get; set; }
    public string? Logo { get; set; }
    public string? CorPrimaria { get; set; } = "#6366f1";
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public string? UsuarioClienteId { get; set; }
    public Usuario? UsuarioCliente { get; set; }

    public ICollection<Conteudo> Conteudos { get; set; } = [];
    public ICollection<Tarefa> Tarefas { get; set; } = [];
    public ICollection<Arquivo> Arquivos { get; set; } = [];
    public ICollection<Financeiro> Financeiros { get; set; } = [];
}
