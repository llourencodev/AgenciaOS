using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Dominios;
using AgenciaOS.Services.Plataforma;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>Domínio próprio da agência (ex.: painel.suaagencia.com.br), com verificação de posse por DNS.</summary>
[Authorize(Roles = "Admin")]
public class DominiosController(ApplicationDbContext db, DominiosService dominios, DominiosOptions opt,
    AuditoriaService auditoria, UserManager<Usuario> userManager, ITenantContext tenant) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["CnameAlvo"] = opt.CnameAlvo;
        ViewData["Subdominio"] = opt.DominioBase is { } b
            ? $"{await db.Agencias.Where(a => a.Id == tenant.AgenciaId).Select(a => a.Slug).FirstAsync(ct)}.{b}" : null;
        return View(await db.DominiosAgencia.OrderByDescending(d => d.Principal).ThenBy(d => d.Host).ToListAsync(ct));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adicionar(string host, CancellationToken ct)
    {
        var (d, erro) = await dominios.AdicionarAsync(host, userManager.GetUserId(User), ct);
        if (d == null) TempData["Erro"] = erro;
        else
        {
            await auditoria.RegistrarAsync("dominios.adicionar", tenant.AgenciaId, "Dominio", d.Host, ct: ct);
            TempData["Sucesso"] = d.Status == StatusDominio.Verificado
                ? $"{d.Host} ativado."
                : $"{d.Host} cadastrado. Crie os registros DNS abaixo e clique em Verificar.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(Services.Contas.RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> Verificar(int id, CancellationToken ct)
    {
        var d = await db.DominiosAgencia.FindAsync([id], ct); // filtro de agência
        if (d == null) return NotFound();
        var ok = await dominios.VerificarAsync(d, ct);
        await auditoria.RegistrarAsync(ok ? "dominios.verificado" : "dominios.verificacao-falhou", tenant.AgenciaId, "Dominio", d.Host, ct: ct);
        if (ok) TempData["Sucesso"] = $"{d.Host} verificado. Falta apenas o certificado HTTPS, configurado pela equipe do AgenciaOS.";
        else TempData["Erro"] = d.UltimoErro;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Principal(int id, CancellationToken ct)
    {
        var lista = await db.DominiosAgencia.ToListAsync(ct);
        if (lista.All(d => d.Id != id)) return NotFound();
        foreach (var d in lista) d.Principal = d.Id == id;
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Remover(int id, CancellationToken ct)
    {
        var d = await db.DominiosAgencia.FindAsync([id], ct);
        if (d == null) return NotFound();
        await dominios.RemoverAsync(d, ct);
        await auditoria.RegistrarAsync("dominios.remover", tenant.AgenciaId, "Dominio", d.Host, ct: ct);
        TempData["Sucesso"] = $"{d.Host} removido.";
        return RedirectToAction(nameof(Index));
    }
}
