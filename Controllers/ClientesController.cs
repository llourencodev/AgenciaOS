using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class ClientesController(ApplicationDbContext db, UserManager<Usuario> userManager) : Controller
{
    public async Task<IActionResult> Index(string? busca)
    {
        var query = db.Clientes.AsQueryable();
        if (!string.IsNullOrWhiteSpace(busca))
            query = query.Where(c => c.NomeEmpresa.Contains(busca) || c.Responsavel.Contains(busca));

        var clientes = await query.OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Busca"] = busca;
        return View(clientes);
    }

    public async Task<IActionResult> Details(int id)
    {
        var cliente = await db.Clientes
            .Include(c => c.Conteudos)
            .Include(c => c.Tarefas).ThenInclude(t => t.Responsavel)
            .Include(c => c.Arquivos)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (cliente == null) return NotFound();
        return View(cliente);
    }

    [Authorize(Roles = "Admin,Equipe")]
    public IActionResult Create() => View(new Cliente());

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(Cliente model)
    {
        if (!ModelState.IsValid) return View(model);
        db.Clientes.Add(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Cliente cadastrado com sucesso!";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Edit(int id)
    {
        var cliente = await db.Clientes.FindAsync(id);
        if (cliente == null) return NotFound();
        return View(cliente);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Edit(Cliente model)
    {
        if (!ModelState.IsValid) return View(model);
        db.Clientes.Update(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Cliente atualizado com sucesso!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Ativar(int id, bool ativo)
    {
        var cliente = await db.Clientes.FindAsync(id);
        if (cliente == null) return NotFound();
        cliente.Ativo = ativo;
        await db.SaveChangesAsync();
        return Ok();
    }
}
