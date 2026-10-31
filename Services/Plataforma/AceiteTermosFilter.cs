using System.Security.Claims;
using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AgenciaOS.Services.Plataforma;

/// <summary>
/// Quando a versão dos Termos ou da Política muda, o usuário precisa aceitar de novo antes de continuar
/// (o aceite fica registrado com versão, data e IP). Pode ser desligado no painel global ou por LGPD_EXIGIR_ACEITE=false.
/// </summary>
public class AceiteTermosFilter(ApplicationDbContext db, ConfiguracaoPlataformaService config, IConfiguration cfg, IMemoryCache cache) : IAsyncActionFilter
{
    private static readonly HashSet<string> Liberados = new(StringComparer.OrdinalIgnoreCase) { "Account", "Conta", "Home", "Marca", "Privacidade", "Webhooks", "Status" };

    public static string ChaveCache(string usuarioId) => $"aceite:{usuarioId}:{DocumentosLegais.VersaoTermos}:{DocumentosLegais.VersaoPrivacidade}";

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var user = ctx.HttpContext.User;
        var controller = ctx.RouteData.Values["controller"]?.ToString() ?? "";
        if (user.Identity?.IsAuthenticated != true || Liberados.Contains(controller) || user.IsInRole("SuperAdmin")
            || string.Equals(cfg["LGPD_EXIGIR_ACEITE"], "false", StringComparison.OrdinalIgnoreCase)
            || !(await config.ObterAsync()).ExigirAceiteTermos)
        {
            await next();
            return;
        }

        var uid = user.FindFirstValue(ClaimTypes.NameIdentifier)!;
        if (!cache.TryGetValue(ChaveCache(uid), out _))
        {
            var aceitos = await db.ConsentimentosTermos
                .Where(c => c.UsuarioId == uid && ((c.Documento == DocumentosLegais.Termos && c.Versao == DocumentosLegais.VersaoTermos)
                                                   || (c.Documento == DocumentosLegais.Privacidade && c.Versao == DocumentosLegais.VersaoPrivacidade)))
                .Select(c => c.Documento).Distinct().CountAsync();
            if (aceitos < 2)
            {
                var volta = ctx.HttpContext.Request.Path + ctx.HttpContext.Request.QueryString;
                ctx.Result = new RedirectToActionResult("AceitarTermos", "Conta", new { returnUrl = volta.ToString() });
                return;
            }
            cache.Set(ChaveCache(uid), true, TimeSpan.FromMinutes(10));
        }
        await next();
    }
}
