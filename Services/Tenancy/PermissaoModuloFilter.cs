using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AgenciaOS.Services.Tenancy;

/// <summary>
/// Filtro global (servidor) que:
/// 1) mantém o superadministrador fora dos módulos das agências (só /Plataforma);
/// 2) bloqueia usuários autenticados sem agência;
/// 3) aplica Usuario.AbasPermitidas, que antes só escondia itens do menu;
/// 4) aplica o bloqueio por assinatura e os módulos do plano (Fase 4).
/// </summary>
public class PermissaoModuloFilter(TenantContext tenant, AcessoAbas acessoAbas, Cobranca.LimitesPlanoService limites) : IAsyncActionFilter
{
    // Controller → aba (mesmos nomes usados em UsuariosController.TodasAbas e no menu)
    private static readonly Dictionary<string, string> Abas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Clientes"] = "Clientes",
        ["Calendario"] = "Calendario",
        ["Arquivos"] = "Arquivos",
        ["Tarefas"] = "Tarefas",
        ["Financeiro"] = "Financeiro",
        ["Contratos"] = "Contratos",
        ["DatasComemorativas"] = "DatasComemorativas",
        ["Marketing"] = "Marketing",
        ["Integracoes"] = "Marketing",
    };

    // Acessíveis sem agência (login, erro, painel da plataforma)
    private static readonly HashSet<string> SemAgencia = new(StringComparer.OrdinalIgnoreCase) { "Account", "Home", "Plataforma", "Marca", "Conta" };

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var user = ctx.HttpContext.User;
        var controller = ctx.RouteData.Values["controller"]?.ToString() ?? "";

        if (user.Identity?.IsAuthenticated != true || SemAgencia.Contains(controller))
        {
            await next();
            return;
        }

        if (user.IsInRole("SuperAdmin") && tenant.AgenciaId == null)
        {
            ctx.Result = new RedirectToActionResult("Index", "Plataforma", null);
            return;
        }

        if (tenant.Modo != ModoTenant.Agencia)
        {
            ctx.Result = new ForbidResult();
            return;
        }

        // Bloqueio comercial por agência (assinatura suspensa/cancelada, teste vencido, agência suspensa).
        // Só a tela de assinatura continua acessível; nenhum dado é apagado e as outras agências não são afetadas.
        if (tenant.Situacao is { } s
            && (s.Status != Models.StatusAgencia.Ativa || (!s.Isenta && Cobranca.CobrancaService.Bloqueada(s.Assinatura, s.TesteExpiraEm, DateTime.UtcNow)))
            && !LiberadoQuandoBloqueado.Contains(controller))
        {
            ctx.Result = new RedirectToActionResult("Bloqueada", "Assinatura", null);
            return;
        }

        if (Abas.TryGetValue(controller, out var aba))
        {
            if (!user.IsInRole("Admin") && !await acessoAbas.PermiteAsync(user, aba))
            {
                ctx.Result = new ForbidResult();
                return;
            }
            if (!await limites.ModuloLiberadoAsync(aba))
            {
                ctx.Result = new RedirectToActionResult("ModuloIndisponivel", "Assinatura", new { modulo = aba });
                return;
            }
        }

        await next();
    }

    private static readonly HashSet<string> LiberadoQuandoBloqueado = new(StringComparer.OrdinalIgnoreCase) { "Assinatura" };
}

/// <summary>A guarda do DbContext barrou uma referência/gravação em outra agência: responde 403 em vez de erro 500.</summary>
public class AcessoEntreAgenciasExceptionFilter(ILogger<AcessoEntreAgenciasExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext ctx)
    {
        if (ctx.Exception is not AcessoEntreAgenciasException e) return;
        logger.LogWarning("Acesso entre agências bloqueado em {Caminho} para {Usuario}: {Motivo}",
            ctx.HttpContext.Request.Path, ctx.HttpContext.User.Identity?.Name, e.Message);
        ctx.Result = new ForbidResult();
        ctx.ExceptionHandled = true;
    }
}

/// <summary>Lê Usuario.AbasPermitidas (vazio = todos os módulos).</summary>
public class AcessoAbas(Microsoft.AspNetCore.Identity.UserManager<Models.Usuario> userManager)
{
    public async Task<bool> PermiteAsync(System.Security.Claims.ClaimsPrincipal user, string aba)
    {
        var u = await userManager.GetUserAsync(user);
        if (u == null || !u.Ativo) return false;
        var abas = u.AbasPermitidas?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return abas.Length == 0 || abas.Contains(aba);
    }
}
