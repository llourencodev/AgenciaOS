namespace AgenciaOS.Models;

public enum StatusDominio { Pendente, Verificado, Falhou, Removido }

/// <summary>
/// Domínio de acesso de uma agência (ex.: painel.agenciaalpha.com.br). Só vale depois de verificado por DNS
/// (TXT com token) e serve para a identidade visual da tela de login e para recusar uso cruzado:
/// a agência efetiva continua sendo sempre a do usuário autenticado.
/// </summary>
public class DominioAgencia : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    /// <summary>Host em minúsculas, sem porta e sem ponto final.</summary>
    public string Host { get; set; } = "";
    public string TokenVerificacao { get; set; } = "";
    public StatusDominio Status { get; set; } = StatusDominio.Pendente;
    public bool Principal { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? VerificadoEm { get; set; }
    public DateTime? UltimaTentativaEm { get; set; }
    public string? UltimoErro { get; set; }
    public string? CriadoPorId { get; set; }

    /// <summary>Registro TXT que o dono do domínio cria para provar a posse.</summary>
    public string NomeRegistroTxt => $"_agenciaos.{Host}";
}
