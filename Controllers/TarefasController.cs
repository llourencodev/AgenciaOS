using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class TarefasController(ApplicationDbContext db, UserManager<Usuario> userManager) : Controller
{
    public async Task<IActionResult> Index(string? status, int? clienteId)
    {
        var usuario = await userManager.GetUserAsync(User);
        var query = db.Tarefas.Include(t => t.Cliente).Include(t => t.Responsavel).AsQueryable();

        if (usuario?.Tipo == TipoUsuario.ColaboradorExterno)
            query = query.Where(t => t.ResponsavelId == usuario.Id);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<StatusTarefa>(status, out var s))
            query = query.Where(t => t.Status == s);

        if (clienteId.HasValue)
            query = query.Where(t => t.ClienteId == clienteId);

        var tarefas = await query.OrderBy(t => t.Prazo).ToListAsync();

        ViewData["StatusFiltro"] = status;
        ViewData["ClienteId"] = clienteId;
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Usuarios"] = await userManager.Users.Where(u => u.Ativo).ToListAsync();

        return View(tarefas);
    }

    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create()
    {
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Usuarios"] = await userManager.Users.Where(u => u.Ativo).ToListAsync();
        return View(new Tarefa());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(Tarefa model)
    {
        var usuario = await userManager.GetUserAsync(User);
        model.CriadoPorId = usuario?.Id;
        model.CriadoEm = DateTime.UtcNow;
        db.Tarefas.Add(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Tarefa criada!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> AlterarStatus(int id, StatusTarefa status)
    {
        var tarefa = await db.Tarefas.FindAsync(id);
        if (tarefa == null) return NotFound();

        var usuario = await userManager.GetUserAsync(User);
        if (usuario?.Tipo == TipoUsuario.ColaboradorExterno && tarefa.ResponsavelId != usuario.Id)
            return Forbid();

        tarefa.Status = status;
        tarefa.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok();
    }

    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Edit(int id)
    {
        var tarefa = await db.Tarefas.FindAsync(id);
        if (tarefa == null) return NotFound();
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Usuarios"] = await userManager.Users.Where(u => u.Ativo).ToListAsync();
        return View(tarefa);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Edit(Tarefa model)
    {
        model.AtualizadoEm = DateTime.UtcNow;
        db.Tarefas.Update(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Tarefa atualizada!";
        return RedirectToAction(nameof(Index));
    }
}
