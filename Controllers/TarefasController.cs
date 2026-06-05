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
    public async Task<IActionResult> Index(string? status, int? clienteId, string? responsavelId, TipoTarefa? tipo)
    {
        var usuario = await userManager.GetUserAsync(User);
        var query = db.Tarefas.Include(t => t.Cliente).Include(t => t.Responsavel).AsQueryable();

        if (usuario?.Tipo == TipoUsuario.ColaboradorExterno)
            query = query.Where(t => t.ResponsavelId == usuario.Id);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<StatusTarefa>(status, out var s))
            query = query.Where(t => t.Status == s);

        if (clienteId.HasValue)
            query = query.Where(t => t.ClienteId == clienteId);

        if (!string.IsNullOrEmpty(responsavelId))
            query = query.Where(t => t.ResponsavelId == responsavelId);

        if (tipo.HasValue)
            query = query.Where(t => t.Tipo == tipo);

        var tarefas = await query.OrderBy(t => t.Prazo).ToListAsync();

        ViewData["StatusFiltro"]  = status;
        ViewData["ClienteId"]     = clienteId;
        ViewData["ResponsavelId"] = responsavelId;
        ViewData["TipoFiltro"]    = tipo;
        ViewData["Clientes"]      = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Usuarios"]      = await userManager.Users.Where(u => u.Ativo).ToListAsync();

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
        if (!ModelState.IsValid)
        {
            ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
            ViewData["Usuarios"] = await userManager.Users.Where(u => u.Ativo).ToListAsync();
            return View(model);
        }

        var usuario = await userManager.GetUserAsync(User);
        model.CriadoPorId = usuario?.Id;
        model.CriadoEm = DateTime.UtcNow;
        db.Tarefas.Add(model);
        await db.SaveChangesAsync();

        if (model.Recorrente && model.FrequenciaRecorrencia.HasValue && model.Prazo.HasValue)
        {
            var fim = model.DataFimRecorrencia ?? model.Prazo.Value.AddYears(1);
            var dataAtual = model.Prazo.Value;

            while (true)
            {
                dataAtual = model.FrequenciaRecorrencia switch
                {
                    Models.FrequenciaRecorrencia.Diaria     => dataAtual.AddDays(1),
                    Models.FrequenciaRecorrencia.Semanal    => dataAtual.AddDays(7),
                    Models.FrequenciaRecorrencia.Quinzenal  => dataAtual.AddDays(15),
                    Models.FrequenciaRecorrencia.Mensal     => dataAtual.AddMonths(1),
                    _ => fim.AddDays(1)
                };

                if (dataAtual > fim) break;

                db.Tarefas.Add(new Tarefa
                {
                    Titulo            = model.Titulo,
                    Descricao         = model.Descricao,
                    Tipo              = model.Tipo,
                    Prioridade        = model.Prioridade,
                    Status            = StatusTarefa.Pendente,
                    Prazo             = dataAtual,
                    ClienteId         = model.ClienteId,
                    ResponsavelId     = model.ResponsavelId,
                    CriadoPorId       = model.CriadoPorId,
                    Recorrente        = true,
                    FrequenciaRecorrencia = model.FrequenciaRecorrencia,
                    DataFimRecorrencia = model.DataFimRecorrencia,
                    CriadoEm          = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }

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
        if (!ModelState.IsValid)
        {
            ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
            ViewData["Usuarios"] = await userManager.Users.Where(u => u.Ativo).ToListAsync();
            return View(model);
        }

        model.AtualizadoEm = DateTime.UtcNow;
        db.Tarefas.Update(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Tarefa atualizada!";
        return RedirectToAction(nameof(Index));
    }
}
