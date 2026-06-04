namespace AgenciaOS.Models;

public class Configuracao
{
    public int Id { get; set; }
    public string NomeAgencia { get; set; } = "AgenciaOS";
    public string Tagline { get; set; } = "Gestão de Agência";
    public string? LogoUrl { get; set; }
    public string? LogoMarcaUrl { get; set; }

    // Cores da marca
    public string CorPrimaria { get; set; } = "#6366f1";
    public string CorPrimariaDark { get; set; } = "#4f46e5";
    public string CorSecundaria { get; set; } = "#8b5cf6";
    public string CorTextoSobrePrimaria { get; set; } = "#ffffff";
    public string CorSidebar { get; set; } = "#0f0d22";

    // Gradiente do sidebar (pode ser cor sólida ou gradiente)
    public string GradienteIcone { get; set; } = "linear-gradient(135deg,#6366f1 0%,#8b5cf6 100%)";
    public string GradienteSaudacao { get; set; } = "linear-gradient(135deg,#6366f1 0%,#8b5cf6 100%)";

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}
