using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Contas;
using AgenciaOS.Services.Plataforma;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>Dados e privacidade da agência (Admin): exportação completa e exclusão de dados pessoais de clientes e usuários.</summary>
[Authorize(Roles = "Admin")]
public class DadosController(ApplicationDbContext db, PrivacidadeService privacidade, AuditoriaService auditoria,
    UserManager<Usuario> userManager, ITenantContext tenant) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Clientes"] = await db.Clientes.OrderBy(c => c.NomeEmpresa).ToListAsync(ct);
        var eu = userManager.GetUserId(User);
        ViewData["Usuarios"] = await userManager.Users.DaAgencia(tenant).Where(u => u.Id != eu && u.Nome != "Titular removido")
            .OrderBy(u => u.Nome).ToListAsync(ct);
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> Exportar(CancellationToken ct)
    {
        var zip = await privacidade.ExportarAgenciaAsync(ct);
        await auditoria.RegistrarAsync("dados.exportar-agencia", tenant.AgenciaId, "Agencia", tenant.AgenciaId?.ToString(), new { bytes = zip.Length }, ct);
        return File(zip, "application/zip", $"agenciaos-exportacao-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AnonimizarCliente(int id, string confirmacao, CancellationToken ct)
    {
        if (confirmacao != "EXCLUIR") { TempData["Erro"] = "Digite EXCLUIR para confirmar."; return RedirectToAction(nameof(Index)); }
        if (!await db.Clientes.AnyAsync(c => c.Id == id, ct)) return NotFound(); // filtro de agência
        await privacidade.AnonimizarClienteAsync(id, ct);
        await auditoria.RegistrarAsync("privacidade.anonimizar-cliente", tenant.AgenciaId, "Cliente", id.ToString(), ct: ct);
        TempData["Sucesso"] = "Dados pessoais do cliente removidos. Lançamentos financeiros e histórico foram mantidos sem identificação.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AnonimizarUsuario(string id, string confirmacao, CancellationToken ct)
    {
        if (confirmacao != "EXCLUIR") { TempData["Erro"] = "Digite EXCLUIR para confirmar."; return RedirectToAction(nameof(Index)); }
        if (id == userManager.GetUserId(User)) return BadRequest();
        if (!await userManager.Users.DaAgencia(tenant).AnyAsync(u => u.Id == id, ct)) return NotFound();
        await privacidade.AnonimizarUsuarioAsync(id, ct);
        await auditoria.RegistrarAsync("privacidade.anonimizar-usuario", tenant.AgenciaId, "Usuario", id, ct: ct);
        TempData["Sucesso"] = "Usuário anonimizado e acesso encerrado.";
        return RedirectToAction(nameof(Index));
    }
}
