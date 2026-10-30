using System.Threading.RateLimiting;

namespace AgenciaOS.Services.Contas;

/// <summary>
/// Limites por IP para login e para ações que disparam e-mail ou criam contas (anti-abuso).
/// Valores lidos a cada requisição, configuráveis por RATE_LIMIT_LOGIN (por 5 min) e RATE_LIMIT_CONTA (por 15 min).
/// Complementa o bloqueio por conta do Identity (5 tentativas → 5 min).
/// </summary>
public static class RateLimitingContas
{
    public const string Login = "login";
    public const string AcoesConta = "conta-sensivel";

    public static IServiceCollection AddRateLimitingContas(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                ctx.HttpContext.Response.Headers.RetryAfter = "300";
                ctx.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.HttpContext.Response.WriteAsync("Muitas tentativas a partir deste endereço. Aguarde alguns minutos e tente novamente.", ct);
            };
            o.AddPolicy(Login, ctx => Particao(ctx, Login, "RATE_LIMIT_LOGIN", 20, TimeSpan.FromMinutes(5)));
            o.AddPolicy(AcoesConta, ctx => Particao(ctx, AcoesConta, "RATE_LIMIT_CONTA", 5, TimeSpan.FromMinutes(15)));
        });
        return services;
    }

    private static RateLimitPartition<string> Particao(HttpContext ctx, string politica, string chaveConfig, int padrao, TimeSpan janela)
    {
        // GET das telas não conta; só o envio dos formulários
        if (HttpMethods.IsGet(ctx.Request.Method)) return RateLimitPartition.GetNoLimiter($"{politica}:get");

        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var limite = int.TryParse(cfg[chaveConfig], out var v) && v > 0 ? v : padrao;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";
        return RateLimitPartition.GetFixedWindowLimiter($"{politica}:{limite}:{ip}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limite, Window = janela, QueueLimit = 0 });
    }
}
