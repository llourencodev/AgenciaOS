namespace AgenciaOS.Models;

/// <summary>Versões vigentes dos documentos legais. Mudou o texto → mude a versão (novo aceite fica registrado).</summary>
public static class DocumentosLegais
{
    public const string Termos = "termos-de-uso";
    public const string Privacidade = "politica-de-privacidade";
    public const string VersaoTermos = "2026-10-30";
    public const string VersaoPrivacidade = "2026-10-30";
}

/// <summary>Registro de aceite de um documento legal (LGPD: quem, qual versão, quando, de onde).</summary>
public class ConsentimentoTermos
{
    public long Id { get; set; }
    public string UsuarioId { get; set; } = "";
    public Usuario? Usuario { get; set; }
    public int? AgenciaId { get; set; }
    public string Documento { get; set; } = "";
    public string Versao { get; set; } = "";
    public DateTime AceitoEm { get; set; } = DateTime.UtcNow;
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
}

/// <summary>Convite para entrar numa agência. Só o hash do token é guardado.</summary>
public class ConviteUsuario : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public string Email { get; set; } = "";
    public string? Nome { get; set; }
    public TipoUsuario Tipo { get; set; } = TipoUsuario.Equipe;
    public string? AbasPermitidas { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiraEm { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public string? CriadoPorId { get; set; }
    public Usuario? CriadoPor { get; set; }
    public DateTime? AceitoEm { get; set; }
    public DateTime? RevogadoEm { get; set; }

    public bool Pendente => AceitoEm == null && RevogadoEm == null && ExpiraEm > DateTime.UtcNow;
}
