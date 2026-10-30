namespace AgenciaOS.Models;

// ══════════════════════════════════════════════════════════════
//  Central de Inteligência de Marketing
// ══════════════════════════════════════════════════════════════

public enum PlataformaMarketing
{
    Instagram,
    Facebook,
    TikTok,
    GoogleAnalytics,
    SearchConsole
}

public enum StatusIntegracao
{
    NaoConectado,
    Conectando,
    Conectado,
    Sincronizando,
    TokenExpirado,
    PermissaoRevogada,
    ErroSincronizacao
}

public enum TipoSincronizacao
{
    Inicial,
    Agendada,
    Manual
}

/// <summary>
/// Autorização OAuth de um cliente numa plataforma. Os tokens ficam criptografados
/// (Data Protection) e nunca saem do servidor.
/// </summary>
public class ConexaoMarketing : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public int ClienteId { get; set; }
    public Cliente? Cliente { get; set; }
    public PlataformaMarketing Plataforma { get; set; }

    public string? UsuarioExternoId { get; set; }
    public string? UsuarioExternoNome { get; set; }

    public string TokenAcessoProtegido { get; set; } = "";
    public string? TokenRefreshProtegido { get; set; }
    public DateTime? ExpiraEm { get; set; }
    public DateTime? RefreshExpiraEm { get; set; }
    public string? Escopos { get; set; }

    public StatusIntegracao Status { get; set; } = StatusIntegracao.Conectando;
    public string? UltimoErro { get; set; }

    public string? ConectadoPorId { get; set; }
    public Usuario? ConectadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public ICollection<ContaMarketing> Contas { get; set; } = [];
}

/// <summary>
/// Conta, página ou propriedade vinculada (perfil do Instagram, página do Facebook,
/// conta TikTok, propriedade GA4 ou site do Search Console).
/// </summary>
public class ContaMarketing : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public int ClienteId { get; set; }
    public Cliente? Cliente { get; set; }
    public int ConexaoMarketingId { get; set; }
    public ConexaoMarketing? Conexao { get; set; }
    public PlataformaMarketing Plataforma { get; set; }

    public string IdExterno { get; set; } = "";
    public string Nome { get; set; } = "";
    public string? NomeUsuario { get; set; }
    public string? FotoUrl { get; set; }
    public string? TipoConta { get; set; }

    /// <summary>Token específico do recurso (ex.: token de página do Facebook), criptografado.</summary>
    public string? TokenRecursoProtegido { get; set; }

    public StatusIntegracao Status { get; set; } = StatusIntegracao.Conectado;
    public string? UltimoErro { get; set; }
    /// <summary>Métricas que a API recusou para esta conta (separadas por vírgula).</summary>
    public string? MetricasIndisponiveis { get; set; }
    public string? Limitacao { get; set; }

    public DateTime? UltimaSincronizacao { get; set; }
    public DateTime? ProximaSincronizacao { get; set; }
    public DateTime? UltimaSolicitacaoManual { get; set; }
    public int FalhasConsecutivas { get; set; }
    public bool Ativa { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public ICollection<MetricaDiaria> Metricas { get; set; } = [];
    public ICollection<PublicacaoMarketing> Publicacoes { get; set; } = [];
}

/// <summary>Valor de uma métrica para uma conta num dia (histórico).</summary>
public class MetricaDiaria
{
    public long Id { get; set; }
    public int ContaMarketingId { get; set; }
    public ContaMarketing? Conta { get; set; }
    public int ClienteId { get; set; }
    public PlataformaMarketing Plataforma { get; set; }
    public DateOnly Data { get; set; }
    public string Metrica { get; set; } = "";
    public decimal Valor { get; set; }
    public string Fonte { get; set; } = "";
    public DateTime ColetadoEm { get; set; } = DateTime.UtcNow;
}

/// <summary>Quebra diária de uma métrica por dimensão (origem, página, palavra-chave, país...).</summary>
public class MetricaDimensao
{
    public long Id { get; set; }
    public int ContaMarketingId { get; set; }
    public ContaMarketing? Conta { get; set; }
    public int ClienteId { get; set; }
    public PlataformaMarketing Plataforma { get; set; }
    public DateOnly Data { get; set; }
    public string Dimensao { get; set; } = "";
    public string ValorDimensao { get; set; } = "";
    public string Metrica { get; set; } = "";
    public decimal Valor { get; set; }
    public DateTime ColetadoEm { get; set; } = DateTime.UtcNow;
}

public class PublicacaoMarketing
{
    public long Id { get; set; }
    public int ContaMarketingId { get; set; }
    public ContaMarketing? Conta { get; set; }
    public int ClienteId { get; set; }
    public PlataformaMarketing Plataforma { get; set; }
    public string IdExterno { get; set; } = "";
    public string? Tipo { get; set; }
    public string? Legenda { get; set; }
    public string? Link { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTime PublicadoEm { get; set; }

    // null = métrica não disponibilizada pela API para esta publicação
    public long? Visualizacoes { get; set; }
    public long? Alcance { get; set; }
    public long? Curtidas { get; set; }
    public long? Comentarios { get; set; }
    public long? Compartilhamentos { get; set; }
    public long? Salvamentos { get; set; }
    public long? Cliques { get; set; }
    public long? Interacoes { get; set; }

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;
}

public class HistoricoSincronizacao
{
    public long Id { get; set; }
    public int ContaMarketingId { get; set; }
    public ContaMarketing? Conta { get; set; }
    public int ClienteId { get; set; }
    public PlataformaMarketing Plataforma { get; set; }
    public TipoSincronizacao Tipo { get; set; }
    public DateTime IniciadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizadoEm { get; set; }
    public bool Sucesso { get; set; }
    public int Tentativa { get; set; } = 1;
    public int RegistrosGravados { get; set; }
    public string? Mensagem { get; set; }
}

public class RelatorioMarketing : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public int ClienteId { get; set; }
    public Cliente? Cliente { get; set; }
    public string Titulo { get; set; } = "";
    public DateOnly PeriodoInicio { get; set; }
    public DateOnly PeriodoFim { get; set; }
    public string Plataformas { get; set; } = "";
    public string? Indicadores { get; set; }
    public bool CompararPeriodoAnterior { get; set; } = true;

    // Preparado para relatórios periódicos enviados por e-mail (ainda não agendados)
    public string? Periodicidade { get; set; }
    public string? EmailsDestino { get; set; }

    public string? GeradoPorId { get; set; }
    public Usuario? GeradoPor { get; set; }
    public DateTime GeradoEm { get; set; } = DateTime.UtcNow;
}
