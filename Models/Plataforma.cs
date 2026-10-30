namespace AgenciaOS.Models;

/// <summary>Trilha de auditoria de ações sensíveis (administração global e da agência, exportações, privacidade).</summary>
public class RegistroAuditoria
{
    public long Id { get; set; }
    public DateTime Quando { get; set; } = DateTime.UtcNow;
    public string? UsuarioId { get; set; }
    public string? UsuarioEmail { get; set; }
    /// <summary>Agência afetada (null = ação global).</summary>
    public int? AgenciaId { get; set; }
    public string Acao { get; set; } = "";
    public string? Entidade { get; set; }
    public string? EntidadeId { get; set; }
    public string? Detalhes { get; set; }
    public string? Ip { get; set; }
}

/// <summary>Configurações globais da plataforma (linha única, Id = 1).</summary>
public class ConfiguracaoPlataforma
{
    public int Id { get; set; } = 1;
    public int DiasTeste { get; set; } = 14;
    public int DiasTolerancia { get; set; } = 7;
    public string? EmailSuporte { get; set; }
    /// <summary>Encarregado pelo tratamento de dados (LGPD, art. 41).</summary>
    public string? EncarregadoNome { get; set; }
    public string? EncarregadoEmail { get; set; }
    /// <summary>Exigir novo aceite quando a versão dos termos/política mudar.</summary>
    public bool ExigirAceiteTermos { get; set; } = true;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}

public enum TipoSolicitacaoPrivacidade
{
    ConfirmacaoTratamento, Acesso, Correcao, Anonimizacao, Exclusao, Portabilidade, InformacaoCompartilhamento, RevogacaoConsentimento
}

public enum StatusSolicitacaoPrivacidade { Aberta, EmAnalise, EncaminhadaAgencia, Concluida, Recusada }

/// <summary>Pedido de titular de dados (LGPD, art. 18). Prazo padrão de resposta: 15 dias.</summary>
public class SolicitacaoPrivacidade
{
    public int Id { get; set; }
    public string Protocolo { get; set; } = "";
    /// <summary>Agência controladora dos dados, quando o pedido é sobre dados inseridos por uma agência.</summary>
    public int? AgenciaId { get; set; }
    public string? UsuarioId { get; set; }
    public string NomeTitular { get; set; } = "";
    public string EmailTitular { get; set; } = "";
    public TipoSolicitacaoPrivacidade Tipo { get; set; }
    public string Descricao { get; set; } = "";
    public StatusSolicitacaoPrivacidade Status { get; set; } = StatusSolicitacaoPrivacidade.Aberta;
    public string? Resposta { get; set; }
    public DateTime CriadaEm { get; set; } = DateTime.UtcNow;
    public DateTime PrazoEm { get; set; } = DateTime.UtcNow.AddDays(15);
    public DateTime? ConcluidaEm { get; set; }
    public string? AtendidaPorId { get; set; }
    public string? Ip { get; set; }

    public bool Atrasada => Status is not (StatusSolicitacaoPrivacidade.Concluida or StatusSolicitacaoPrivacidade.Recusada) && PrazoEm < DateTime.UtcNow;
}
