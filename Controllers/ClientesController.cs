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
public class ClientesController(ApplicationDbContext db, UserManager<Usuario> userManager, AcessoService acesso, ITenantContext tenant) : Controller
{
    public async Task<IActionResult> Index(string? busca)
    {
        var escopo = await acesso.ObterAsync(User);
        if (escopo is { EhCliente: true, ClienteProprioId: int proprio })
            return RedirectToAction(nameof(Details), new { id = proprio });

        var query = acesso.Clientes(escopo);
        if (!string.IsNullOrWhiteSpace(busca))
            query = query.Where(c => c.NomeEmpresa.Contains(busca) || c.Responsavel.Contains(busca));

        var clientes = await query.OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Busca"] = busca;
        return View(clientes);
    }

    public async Task<IActionResult> Details(int id)
    {
        var escopo = await acesso.ObterAsync(User);
        if (escopo == null) return NotFound();

        var query = acesso.Clientes(escopo).Where(c => c.Id == id).Include(c => c.Conteudos).Include(c => c.Arquivos);
        // Colaborador externo só enxerga as próprias tarefas do cliente
        var uid = escopo.Usuario.Id;
        var cliente = escopo.EhColaboradorExterno
            ? await query.Include(c => c.Tarefas.Where(t => t.ResponsavelId == uid)).ThenInclude(t => t.Responsavel).FirstOrDefaultAsync()
            : await query.Include(c => c.Tarefas).ThenInclude(t => t.Responsavel).FirstOrDefaultAsync();

        if (cliente == null) return NotFound();
        return View(cliente);
    }

    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create()
    {
        await CarregarUsuariosCliente(null);
        return View(new Cliente());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Create(Cliente model)
    {
        if (!ModelState.IsValid)
        {
            await CarregarUsuariosCliente(null);
            return View(model);
        }

        // Id e agência nunca vêm do formulário
        model.Id = 0;
        model.AgenciaId = 0;
        model.CriadoEm = DateTime.UtcNow;
        model.UsuarioClienteId = string.IsNullOrEmpty(model.UsuarioClienteId) ? null : model.UsuarioClienteId;
        if (!await UsuarioClienteValido(model.UsuarioClienteId, null))
        {
            ModelState.AddModelError(nameof(Cliente.UsuarioClienteId), "Usuário de acesso inválido ou já vinculado a outro cliente.");
            await CarregarUsuariosCliente(null);
            return View(model);
        }

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
        await CarregarUsuariosCliente(cliente.Id);
        return View(cliente);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Edit(Cliente model)
    {
        // Carrega da agência atual (filtro global) e copia só os campos editáveis: evita over-posting e IDs forjados
        var cliente = await db.Clientes.FindAsync(model.Id);
        if (cliente == null) return NotFound();

        var usuarioClienteId = string.IsNullOrEmpty(model.UsuarioClienteId) ? null : model.UsuarioClienteId;
        if (ModelState.IsValid && !await UsuarioClienteValido(usuarioClienteId, cliente.Id))
            ModelState.AddModelError(nameof(Cliente.UsuarioClienteId), "Usuário de acesso inválido ou já vinculado a outro cliente.");
        if (!ModelState.IsValid)
        {
            await CarregarUsuariosCliente(cliente.Id);
            return View(model);
        }

        cliente.NomeEmpresa = model.NomeEmpresa;
        cliente.Responsavel = model.Responsavel;
        cliente.Telefone = model.Telefone;
        cliente.Email = model.Email;
        cliente.Instagram = model.Instagram;
        cliente.PlanoContratado = model.PlanoContratado;
        cliente.Observacoes = model.Observacoes;
        cliente.Estrategia = model.Estrategia;
        cliente.Objetivos = model.Objetivos;
        cliente.CorPrimaria = model.CorPrimaria;
        cliente.Ativo = model.Ativo;
        cliente.UsuarioClienteId = usuarioClienteId;

        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Cliente atualizado com sucesso!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Ativar(int id, bool ativo)
    {
        var cliente = await db.Clientes.FindAsync(id);
        if (cliente == null) return NotFound();
        cliente.Ativo = ativo;
        await db.SaveChangesAsync();
        return Ok();
    }

    /// <summary>Usuário do tipo Cliente, da mesma agência, ainda não vinculado a outro cliente.</summary>
    private async Task<bool> UsuarioClienteValido(string? usuarioId, int? clienteAtualId)
    {
        if (usuarioId == null) return true;
        var ehCliente = await userManager.Users.DaAgencia(tenant)
            .AnyAsync(u => u.Id == usuarioId && u.Tipo == TipoUsuario.Cliente);
        if (!ehCliente) return false;
        return !await db.Clientes.AnyAsync(c => c.UsuarioClienteId == usuarioId && c.Id != clienteAtualId);
    }

    private async Task CarregarUsuariosCliente(int? clienteAtualId)
    {
        var vinculados = await db.Clientes
            .Where(c => c.UsuarioClienteId != null && c.Id != clienteAtualId)
            .Select(c => c.UsuarioClienteId!).ToListAsync();
        ViewData["UsuariosCliente"] = await userManager.Users.DaAgencia(tenant)
            .Where(u => u.Tipo == TipoUsuario.Cliente && u.Ativo && !vinculados.Contains(u.Id))
            .OrderBy(u => u.Nome).ToListAsync();
    }
}
