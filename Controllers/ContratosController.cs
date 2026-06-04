using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize(Roles = "Admin,Equipe")]
public class ContratosController(ApplicationDbContext db, UserManager<Usuario> userManager) : Controller
{
    public async Task<IActionResult> Index(int? clienteId, StatusContrato? status)
    {
        var query = db.Contratos.Include(c => c.Cliente).Include(c => c.CriadoPor).AsQueryable();

        if (clienteId.HasValue) query = query.Where(c => c.ClienteId == clienteId);
        if (status.HasValue)    query = query.Where(c => c.Status == status);

        var lista = await query.OrderByDescending(c => c.CriadoEm).ToListAsync();

        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["ClienteId"] = clienteId;
        ViewData["StatusFiltro"] = status;
        return View(lista);
    }

    public async Task<IActionResult> Create(int? clienteId)
    {
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["ClienteIdPre"] = clienteId;
        return View(new Contrato());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Contrato model)
    {
        var usuario = await userManager.GetUserAsync(User);
        model.CriadoPorId = usuario?.Id;
        model.CriadoEm = DateTime.UtcNow;
        db.Contratos.Add(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Contrato criado!";
        return RedirectToAction(nameof(Details), new { id = model.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var contrato = await db.Contratos.Include(c => c.Cliente).Include(c => c.CriadoPor).FirstOrDefaultAsync(c => c.Id == id);
        if (contrato == null) return NotFound();
        return View(contrato);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlterarStatus(int id, StatusContrato status)
    {
        var c = await db.Contratos.FindAsync(id);
        if (c == null) return NotFound();
        c.Status = status;
        if (status == StatusContrato.Assinado) c.DataAssinatura = DateTime.UtcNow;
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Status atualizado!";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Excluir(int id)
    {
        var c = await db.Contratos.FindAsync(id);
        if (c == null) return NotFound();
        db.Contratos.Remove(c);
        await db.SaveChangesAsync();
        return Ok();
    }
}
