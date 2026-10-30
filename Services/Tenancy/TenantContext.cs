using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace AgenciaOS.Services.Tenancy;

public enum ModoTenant
{
    /// <summary>Sem agência (anônimo ou superadmin): dados de agência ficam invisíveis e não podem ser gravados.</summary>
    Nenhum,
    /// <summary>Requisição de um usuário de agência: tudo filtrado e verificado por <see cref="ITenantContext.AgenciaId"/>.</summary>
    Agencia,
    /// <summary>Código interno (worker, seed, painel da plataforma): sem filtro. Ativado só explicitamente.</summary>
    Sistema
}

/// <summary>Agência da requisição/escopo atual. Nunca é preenchida a partir de dados enviados pelo navegador.</summary>
public interface ITenantContext
{
    ModoTenant Modo { get; }
    int? AgenciaId { get; }
}

public sealed class TenantContext : ITenantContext
{
    public ModoTenant Modo { get; private set; } = ModoTenant.Nenhum;
    public int? AgenciaId { get; private set; }

    public void UsarAgencia(int agenciaId)
    {
        if (agenciaId <= 0) throw new ArgumentOutOfRangeException(nameof(agenciaId));
        Modo = ModoTenant.Agencia;
        AgenciaId = agenciaId;
    }

    public void UsarSistema()
    {
        Modo = ModoTenant.Sistema;
        AgenciaId = null;
    }

    public static TenantContext ParaAgencia(int agenciaId)
    {
        var t = new TenantContext();
        t.UsarAgencia(agenciaId);
        return t;
    }
}

/// <summary>Tentativa de ler/gravar dados fora da agência do usuário.</summary>
public class AcessoEntreAgenciasException(string mensagem) : InvalidOperationException(mensagem);

/// <summary>
/// Resolve a agência a partir do usuário autenticado (consulta ao banco, nunca da requisição).
/// Deve rodar depois de UseAuthentication e antes de UseAuthorization.
/// </summary>
public class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantContext tenant, ApplicationDbContext db)
    {
        if (http.User.Identity?.IsAuthenticated == true
            && http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } usuarioId)
        {
            var agenciaId = await db.Users.Where(u => u.Id == usuarioId).Select(u => u.AgenciaId).FirstOrDefaultAsync();
            if (agenciaId is int id) tenant.UsarAgencia(id);
        }
        await next(http);
    }
}

public static class TenantExtensions
{
    /// <summary>Escopo de DI para código interno (workers, seed). O tenant fica em modo Sistema.</summary>
    public static IServiceScope CriarEscopoSistema(this IServiceScopeFactory fabrica)
    {
        var escopo = fabrica.CreateScope();
        escopo.ServiceProvider.GetRequiredService<TenantContext>().UsarSistema();
        return escopo;
    }

    /// <summary>Usuários da agência atual. <c>Usuario</c> não tem filtro global (o Identity precisa dele no login).</summary>
    public static IQueryable<Usuario> DaAgencia(this IQueryable<Usuario> usuarios, ITenantContext tenant)
    {
        var id = tenant.AgenciaId;
        return tenant.Modo == ModoTenant.Sistema ? usuarios : usuarios.Where(u => id != null && u.AgenciaId == id);
    }
}
