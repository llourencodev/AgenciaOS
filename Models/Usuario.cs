using Microsoft.AspNetCore.Identity;

namespace AgenciaOS.Models;

public class Usuario : IdentityUser
{
    public string Nome { get; set; } = "";
    public string? Sobrenome { get; set; }
    public string? Foto { get; set; }
    public TipoUsuario Tipo { get; set; } = TipoUsuario.Equipe;
    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? UltimoAcesso { get; set; }

    public string NomeCompleto => $"{Nome} {Sobrenome}".Trim();
    public string Iniciais => $"{Nome.FirstOrDefault()}{Sobrenome?.FirstOrDefault()}".ToUpper();

    public ICollection<Tarefa> TarefasAtribuidas { get; set; } = [];
    public ICollection<Notificacao> Notificacoes { get; set; } = [];
}

public enum TipoUsuario
{
    Admin,
    Equipe,
    Cliente,
    ColaboradorExterno
}
