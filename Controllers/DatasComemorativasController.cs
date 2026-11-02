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
        var anoAtual = DateTime.Now.Year;

        // Datas do Brasil entram sozinhas: na primeira visita da agência e a cada ano novo
        // (datas móveis como Carnaval e Páscoa mudam de ano para ano)
        if (!await db.DatasComemorativas.AnyAsync(d => !d.Anual && d.Ano == anoAtual))
            await ImportarAsync(anoAtual);

        // Datas móveis de anos que já passaram não aparecem mais
        var todas = await db.DatasComemorativas.Where(d => d.Anual || d.Ano == null || d.Ano >= anoAtual)
            .OrderBy(d => d.Mes).ThenBy(d => d.Dia).ToListAsync();
        ViewData["MesFiltro"] = mes;
        return View(todas);
    }

    private async Task<int> ImportarAsync(int ano)
    {
        var existentes = await db.DatasComemorativas.ToListAsync();
        int adicionadas = 0;
        foreach (var d in BrasilDatasService.ObterParaAno(ano))
        {
            var jaExiste = existentes.Any(e =>
                e.Nome == d.Nome &&
                e.Dia  == d.Dia  &&
                e.Mes  == d.Mes  &&
                (d.Anual ? e.Anual : e.Ano == d.Ano));
            if (jaExiste) continue;
            db.DatasComemorativas.Add(d);
            adicionadas++;
        }
        await db.SaveChangesAsync();
        return adicionadas;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(DataComemorativa model)
    {
        model.Id = 0; model.AgenciaId = 0; // nunca vêm do formulário
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
        var adicionadas = await ImportarAsync(anoImportar);

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
