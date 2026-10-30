using AgenciaOS.Models;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Credenciais dos aplicativos externos. Lidas de variáveis de ambiente
/// (ou appsettings, que o ASP.NET também mescla). Nunca expor estes valores em views ou respostas.
/// </summary>
public class MarketingOptions
{
    public string? AppBaseUrl { get; init; }

    public string? MetaAppId { get; init; }
    public string? MetaAppSecret { get; init; }
    public string MetaGraphVersion { get; init; } = "v23.0";

    public string? InstagramAppId { get; init; }
    public string? InstagramAppSecret { get; init; }
    public string InstagramGraphVersion { get; init; } = "v23.0";

    public string? TikTokClientKey { get; init; }
    public string? TikTokClientSecret { get; init; }

    public string? GoogleClientId { get; init; }
    public string? GoogleClientSecret { get; init; }

    public bool SincronizacaoHabilitada { get; init; } = true;

    public static MarketingOptions Carregar(IConfiguration cfg) => new()
    {
        AppBaseUrl            = cfg["APP_BASE_URL"],
        MetaAppId             = cfg["META_APP_ID"],
        MetaAppSecret         = cfg["META_APP_SECRET"],
        MetaGraphVersion      = cfg["META_GRAPH_VERSION"] ?? "v23.0",
        InstagramAppId        = cfg["INSTAGRAM_APP_ID"],
        InstagramAppSecret    = cfg["INSTAGRAM_APP_SECRET"],
        InstagramGraphVersion = cfg["INSTAGRAM_GRAPH_VERSION"] ?? "v23.0",
        TikTokClientKey       = cfg["TIKTOK_CLIENT_KEY"],
        TikTokClientSecret    = cfg["TIKTOK_CLIENT_SECRET"],
        GoogleClientId        = cfg["GOOGLE_CLIENT_ID"],
        GoogleClientSecret    = cfg["GOOGLE_CLIENT_SECRET"],
        SincronizacaoHabilitada = !string.Equals(cfg["MARKETING_SYNC_ENABLED"], "false", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>Nomes das variáveis de ambiente que faltam para a plataforma funcionar.</summary>
    public string[] VariaveisPendentes(PlataformaMarketing p)
    {
        var faltando = new List<string>();
        void Checar(string? valor, string nome) { if (string.IsNullOrWhiteSpace(valor)) faltando.Add(nome); }
        switch (p)
        {
            case PlataformaMarketing.Instagram:
                Checar(InstagramAppId, "INSTAGRAM_APP_ID");
                Checar(InstagramAppSecret, "INSTAGRAM_APP_SECRET");
                break;
            case PlataformaMarketing.Facebook:
                Checar(MetaAppId, "META_APP_ID");
                Checar(MetaAppSecret, "META_APP_SECRET");
                break;
            case PlataformaMarketing.TikTok:
                Checar(TikTokClientKey, "TIKTOK_CLIENT_KEY");
                Checar(TikTokClientSecret, "TIKTOK_CLIENT_SECRET");
                break;
            case PlataformaMarketing.GoogleAnalytics:
            case PlataformaMarketing.SearchConsole:
                Checar(GoogleClientId, "GOOGLE_CLIENT_ID");
                Checar(GoogleClientSecret, "GOOGLE_CLIENT_SECRET");
                break;
        }
        return [.. faltando];
    }

    /// <summary>Intervalo entre sincronizações automáticas, respeitando limites de cada provedor.</summary>
    public static TimeSpan IntervaloSincronizacao(PlataformaMarketing p) => p switch
    {
        PlataformaMarketing.Instagram       => TimeSpan.FromHours(6),
        PlataformaMarketing.Facebook        => TimeSpan.FromHours(6),
        PlataformaMarketing.TikTok          => TimeSpan.FromHours(12),
        PlataformaMarketing.GoogleAnalytics => TimeSpan.FromHours(6),
        PlataformaMarketing.SearchConsole   => TimeSpan.FromHours(12),
        _ => TimeSpan.FromHours(12)
    };
}
