using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
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
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> ImportarBrasil(int? ano)
    {
        var anoImportar = ano ?? DateTime.Now.Year;
        var datas = BrasilDatasService.ObterParaAno(anoImportar);

        // Busca existentes para evitar duplicatas
        var existentes = await db.DatasComemorativas.ToListAsync();

        int adicionadas = 0;
        foreach (var d in datas)
        {
            bool jáExiste = existentes.Any(e =>
                e.Nome == d.Nome &&
                e.Dia  == d.Dia  &&
                e.Mes  == d.Mes  &&
                (d.Anual ? e.Anual : e.Ano == d.Ano));

            if (!jáExiste)
            {
                db.DatasComemorativas.Add(d);
                adicionadas++;
            }
        }

        await db.SaveChangesAsync();

        TempData["Sucesso"] = adicionadas > 0
            ? $"{adicionadas} datas do Brasil importadas para {anoImportar}!"
            : $"Todas as datas de {anoImportar} já estavam cadastradas.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
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
