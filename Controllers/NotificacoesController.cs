using AgenciaOS.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgenciaOS.Models;

namespace AgenciaOS.Controllers;

[Authorize]
public class NotificacoesController(ApplicationDbContext db, UserManager<Usuario> userManager) : Controller
{
    public async Task<IActionResult> Index()
    {
        var usuario = await userManager.GetUserAsync(User);
        var notifs = await db.Notificacoes
            .Where(n => n.UsuarioId == usuario!.Id)
            .OrderByDescending(n => n.CriadoEm)
            .Take(50)
            .ToListAsync();
        return View(notifs);
    }

    [HttpPost]
    public async Task<IActionResult> Marcar(int id)
    {
        var notif = await db.Notificacoes.FindAsync(id);
        if (notif == null) return NotFound();
        notif.Lida = true;
        await db.SaveChangesAsync();
        return Ok();
    }

    [HttpPost]
    public async Task<IActionResult> MarcarTodas()
    {
        var usuario = await userManager.GetUserAsync(User);
        await db.Notificacoes
            .Where(n => n.UsuarioId == usuario!.Id && !n.Lida)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Lida, true));
        return Ok();
    }

    [HttpGet]
    public async Task<IActionResult> Count()
    {
        var usuario = await userManager.GetUserAsync(User);
        var count = await db.Notificacoes.CountAsync(n => n.UsuarioId == usuario!.Id && !n.Lida);
        return Json(count);
    }

    [HttpGet]
    public async Task<IActionResult> Recentes()
    {
        var usuario = await userManager.GetUserAsync(User);
        var notifs = await db.Notificacoes
            .Where(n => n.UsuarioId == usuario!.Id && !n.Lida)
            .OrderByDescending(n => n.CriadoEm)
            .Take(5)
            .Select(n => new { n.Id, n.Titulo, n.Mensagem, n.Url, n.IconeCss, tempo = n.CriadoEm })
            .ToListAsync();
        return Json(notifs);
    }
}
