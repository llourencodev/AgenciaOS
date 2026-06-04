using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class DatasComemorativasController(ApplicationDbContext db) : Controller
{
    public async Task<IActionResult> Index(int? mes)
    {
        mes ??= DateTime.Now.Month;
        var todas = await db.DatasComemorativas.OrderBy(d => d.Mes).ThenBy(d => d.Dia).ToListAsync();
        ViewData["MesFiltro"] = mes;
        return View(todas);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(DataComemorativa model)
    {
        model.CriadoEm = DateTime.UtcNow;
        db.DatasComemorativas.Add(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Data adicionada!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Excluir(int id)
    {
        var d = await db.DatasComemorativas.FindAsync(id);
        if (d == null) return NotFound();
        db.DatasComemorativas.Remove(d);
        await db.SaveChangesAsync();
        return Ok();
    }
}
