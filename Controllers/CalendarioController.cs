using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class CalendarioController(ApplicationDbContext db, UserManager<Usuario> userManager, AcessoService acesso) : Controller
{
    /// <summary>Conteúdos dos clientes que o usuário pode ver (a agência já é filtrada pelo DbContext).</summary>
    private IQueryable<Conteudo> ConteudosVisiveis(AcessoService.Escopo? escopo)
    {
        var clientes = acesso.Clientes(escopo).Select(c => c.Id);
        return db.Conteudos.Where(c => clientes.Contains(c.ClienteId));
    }

    public async Task<IActionResult> Index(int? clienteId, int? mes, int? ano)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;

        var escopo = await acesso.ObterAsync(User);
        var query = ConteudosVisiveis(escopo).Include(c => c.Cliente).AsQueryable();
        if (clienteId.HasValue)
            query = query.Where(c => c.ClienteId == clienteId);

        var conteudos = await query
            .Where(c => c.DataPrevista.Month == mes && c.DataPrevista.Year == ano)
            .OrderBy(c => c.DataPrevista)
            .ToListAsync();

        ViewData["Mes"] = mes;
        ViewData["Ano"] = ano;
        ViewData["ClienteId"] = clienteId;
        ViewData["Clientes"] = await acesso.Clientes(escopo).Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();

        return View(conteudos);
    }

    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(int? clienteId, DateTime? data)
    {
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        return View(new Conteudo { ClienteId = clienteId ?? 0, DataPrevista = data ?? DateTime.Today });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(Conteudo model)
    {
        // Cliente precisa ser da agência atual (filtro global); a guarda do DbContext verifica de novo
        if (!await db.Clientes.AnyAsync(c => c.Id == model.ClienteId)) return NotFound();

        var usuario = await userManager.GetUserAsync(User);
        model.Id = 0;
        model.AgenciaId = 0;
        model.CriadoPorId = usuario?.Id;
        model.CriadoEm = DateTime.UtcNow;
        model.AtualizadoEm = DateTime.UtcNow;

        db.Conteudos.Add(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Conteúdo criado!";
        return RedirectToAction(nameof(Index), new { clienteId = model.ClienteId });
    }

    public async Task<IActionResult> Details(int id)
    {
        var escopo = await acesso.ObterAsync(User);
        var conteudo = await ConteudosVisiveis(escopo)
            .Include(c => c.Cliente)
            .Include(c => c.Comentarios).ThenInclude(co => co.Usuario)
            .Include(c => c.Historicos).ThenInclude(h => h.Usuario)
            .FirstOrDefaultAsync(c => c.Id == id);

        if (conteudo == null) return NotFound();
        return View(conteudo);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> AlterarStatus(int id, StatusConteudo status)
    {
        var conteudo = await db.Conteudos.FindAsync(id);
        if (conteudo == null) return NotFound();

        var usuario = await userManager.GetUserAsync(User);
        var anterior = conteudo.Status;
        conteudo.Status = status;
        conteudo.AtualizadoEm = DateTime.UtcNow;

        db.HistoricosConteudo.Add(new HistoricoConteudo
        {
            ConteudoId = id,
            UsuarioId = usuario!.Id,
            StatusAnterior = anterior,
            StatusNovo = status,
            Descricao = $"Status alterado de {anterior} para {status}"
        });

        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Comentar(int conteudoId, string texto)
    {
        var escopo = await acesso.ObterAsync(User);
        if (escopo == null || !await ConteudosVisiveis(escopo).AnyAsync(c => c.Id == conteudoId)) return NotFound();

        if (string.IsNullOrWhiteSpace(texto))
            return RedirectToAction(nameof(Details), new { id = conteudoId });

        db.ComentariosConteudo.Add(new ComentarioConteudo
        {
            ConteudoId = conteudoId,
            UsuarioId = escopo.Usuario.Id,
            Texto = texto.Trim()
        });
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id = conteudoId });
    }

    [HttpGet]
    public async Task<IActionResult> Eventos(int? clienteId, int mes, int ano)
    {
        var escopo = await acesso.ObterAsync(User);
        var query = ConteudosVisiveis(escopo).Include(c => c.Cliente).AsQueryable();
        if (clienteId.HasValue) query = query.Where(c => c.ClienteId == clienteId);

        var conteudos = await query
            .Where(c => c.DataPrevista.Month == mes && c.DataPrevista.Year == ano)
            .Select(c => new {
                id = c.Id,
                title = c.Titulo,
                start = c.DataPrevista.ToString("yyyy-MM-dd"),
                className = StatusCss(c.Status),
                extendedProps = new { tipo = c.Tipo.ToString(), status = c.Status.ToString(), cliente = c.Cliente != null ? c.Cliente.NomeEmpresa : "" }
            }).ToListAsync();

        return Json(conteudos);
    }

    private static string StatusCss(StatusConteudo s) => s switch
    {
        StatusConteudo.Aprovado => "event-approved",
        StatusConteudo.Publicado => "event-published",
        StatusConteudo.AguardandoAprovacao => "event-waiting",
        StatusConteudo.AjusteSolicitado => "event-adjust",
        _ => "event-default"
    };
}
