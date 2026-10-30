using System.Threading.RateLimiting;
using AgenciaOS.Services.Marketing.Provedores;

namespace AgenciaOS.Services.Marketing;

public static class MarketingServiceCollectionExtensions
{
    public const string PoliticaRateLimit = "marketing";

    public static IServiceCollection AddCentralMarketing(this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton(MarketingOptions.Carregar(cfg));

        // Sem loggers do HttpClient: as URLs da Graph API carregam access_token na query string
        services.AddHttpClient(ClienteHttpMarketing.NomeCliente, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AgenciaOS-Marketing/1.0");
        }).RemoveAllLoggers();

        services.AddSingleton<ClienteHttpMarketing>();
        services.AddSingleton<ProtetorTokens>();
        services.AddSingleton<OAuthStateService>();
        services.AddSingleton<FilaSincronizacao>();

        services.AddSingleton<IProvedorMarketing, InstagramProvedor>();
        services.AddSingleton<IProvedorMarketing, FacebookProvedor>();
        services.AddSingleton<IProvedorMarketing, TikTokProvedor>();
        services.AddSingleton<IProvedorMarketing, GoogleAnalyticsProvedor>();
        services.AddSingleton<IProvedorMarketing, SearchConsoleProvedor>();
        services.AddSingleton<RegistroProvedores>();

        services.AddScoped<AcessoMarketingService>();
        services.AddScoped<SincronizacaoMarketingService>();
        services.AddScoped<IntegracaoMarketingService>();
        services.AddScoped<PainelMarketingService>();
        services.AddScoped<IGeradorInsights, InsightsMarketingService>();
        services.AddScoped<RelatorioMarketingPdfService>();
        services.AddHostedService<SincronizacaoMarketingWorker>();

        // Limita conexões/atualizações por usuário (protege as cotas das APIs externas)
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(PoliticaRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        return services;
    }
}
