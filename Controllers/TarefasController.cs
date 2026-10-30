using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class TarefasController(ApplicationDbContext db, UserManager<Usuario> userManager, AcessoService acesso, ITenantContext tenant) : Controller
{
    private Task<List<Usuario>> UsuariosDaAgencia() =>
        userManager.Users.DaAgencia(tenant).Where(u => u.Ativo).ToListAsync();

    public async Task<IActionResult> Index(string? status, int? clienteId, string? responsavelId, TipoTarefa? tipo)
    {
        var escopo = await acesso.ObterAsync(User);
        var query = db.Tarefas.Include(t => t.Cliente).Include(t => t.Responsavel).AsQueryable();

        if (escopo == null)
            query = query.Where(_ => false);
        else if (escopo.EhColaboradorExterno)
            query = query.Where(t => t.ResponsavelId == escopo.Usuario.Id);
        else if (escopo.EhCliente)
        {
            var proprio = escopo.ClienteProprioId;
            query = query.Where(t => proprio != null && t.ClienteId == proprio);
        }
        else if (!escopo.EhInterno)
            query = query.Where(_ => false);

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
        ViewData["Clientes"]      = await acesso.Clientes(escopo).Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Usuarios"]      = escopo?.EhInterno == true ? await UsuariosDaAgencia() : new List<Usuario>();

        return View(tarefas);
    }

    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create()
    {
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Usuarios"] = await UsuariosDaAgencia();
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
            ViewData["Usuarios"] = await UsuariosDaAgencia();
            return View(model);
        }

        var usuario = await userManager.GetUserAsync(User);
        model.Id = 0;
        model.AgenciaId = 0;
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlterarStatus(int id, StatusTarefa status)
    {
        var tarefa = await db.Tarefas.FindAsync(id);
        if (tarefa == null) return NotFound();

        var escopo = await acesso.ObterAsync(User);
        if (escopo == null || escopo.EhCliente) return Forbid();
        if (escopo.EhColaboradorExterno && tarefa.ResponsavelId != escopo.Usuario.Id)
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
        ViewData["Usuarios"] = await UsuariosDaAgencia();
        return View(tarefa);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Edit(Tarefa model)
    {
        // Carrega da agência atual e copia só os campos editáveis (sem Update() da entidade do formulário)
        var tarefa = await db.Tarefas.FindAsync(model.Id);
        if (tarefa == null) return NotFound();

        if (!ModelState.IsValid)
        {
            ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
            ViewData["Usuarios"] = await UsuariosDaAgencia();
            return View(model);
        }

        tarefa.Titulo = model.Titulo;
        tarefa.Descricao = model.Descricao;
        tarefa.Tipo = model.Tipo;
        tarefa.Prioridade = model.Prioridade;
        tarefa.Status = model.Status;
        tarefa.Prazo = model.Prazo;
        tarefa.ClienteId = model.ClienteId;
        tarefa.ResponsavelId = string.IsNullOrEmpty(model.ResponsavelId) ? null : model.ResponsavelId;
        tarefa.AtualizadoEm = DateTime.UtcNow;

        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Tarefa atualizada!";
        return RedirectToAction(nameof(Index));
    }
}
