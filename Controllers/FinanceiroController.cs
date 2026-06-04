using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize(Roles = "Admin,Equipe")]
public class FinanceiroController(ApplicationDbContext db, UserManager<Usuario> userManager) : Controller
{
    private async Task CarregarViewData(int mes, int ano)
    {
        // Totais do mês inteiro para o resumo
        var todos = await db.Financeiros
            .Where(f => f.DataVencimento.Month == mes && f.DataVencimento.Year == ano)
            .ToListAsync();

        ViewData["Mes"]      = mes;
        ViewData["Ano"]      = ano;
        ViewData["Receita"]  = todos.Where(f => f.Tipo == TipoFinanceiro.Entrada && f.Pago).Sum(f => f.Valor);
        ViewData["Despesa"]  = todos.Where(f => f.Tipo == TipoFinanceiro.Saida && f.Pago).Sum(f => f.Valor);
        ViewData["Lucro"]    = todos.Where(f => f.Tipo == TipoFinanceiro.Entrada && f.Pago).Sum(f => f.Valor)
                             - todos.Where(f => f.Tipo == TipoFinanceiro.Saida && f.Pago).Sum(f => f.Valor);
        ViewData["AReceber"] = todos.Where(f => f.Tipo == TipoFinanceiro.Entrada && !f.Pago).Sum(f => f.Valor);
        ViewData["APagar"]   = todos.Where(f => f.Tipo == TipoFinanceiro.Saida && !f.Pago).Sum(f => f.Valor);
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
    }

    // ── VISÃO GERAL ──────────────────────────────
    public async Task<IActionResult> Index(int? mes, int? ano)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;
        await CarregarViewData(mes.Value, ano.Value);
        return View();
    }

    // ── CONTAS A RECEBER ─────────────────────────
    public async Task<IActionResult> ContasReceber(int? mes, int? ano, bool? vencidas, int? clienteId)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;

        var query = db.Financeiros.Include(f => f.Cliente)
            .Where(f => f.Tipo == TipoFinanceiro.Entrada
                     && f.DataVencimento.Month == mes && f.DataVencimento.Year == ano)
            .AsQueryable();

        if (vencidas == true)  query = query.Where(f => !f.Pago && f.DataVencimento < DateTime.Now);
        if (clienteId.HasValue) query = query.Where(f => f.ClienteId == clienteId);

        var lista = await query.OrderBy(f => f.DataVencimento).ToListAsync();
        await CarregarViewData(mes.Value, ano.Value);

        ViewData["Vencidas"]  = vencidas;
        ViewData["ClienteId"] = clienteId;
        return View(lista);
    }

    // ── CONTAS A PAGAR ───────────────────────────
    public async Task<IActionResult> ContasPagar(int? mes, int? ano, bool? vencidas, CategoriaFinanceiro? categoria)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;

        var query = db.Financeiros.Include(f => f.Cliente)
            .Where(f => f.Tipo == TipoFinanceiro.Saida
                     && f.DataVencimento.Month == mes && f.DataVencimento.Year == ano)
            .AsQueryable();

        if (vencidas == true)      query = query.Where(f => !f.Pago && f.DataVencimento < DateTime.Now);
        if (categoria.HasValue)    query = query.Where(f => f.Categoria == categoria);

        var lista = await query.OrderBy(f => f.DataVencimento).ToListAsync();
        await CarregarViewData(mes.Value, ano.Value);

        ViewData["Vencidas"]  = vencidas;
        ViewData["Categoria"] = categoria;
        return View(lista);
    }

    // ── CREATE ───────────────────────────────────
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Financeiro model)
    {
        var usuario = await userManager.GetUserAsync(User);
        model.CriadoPorId = usuario?.Id;
        model.CriadoEm = DateTime.UtcNow;
        db.Financeiros.Add(model);
        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Lançamento criado com sucesso!";

        // Redireciona para a aba correta
        return model.Tipo == TipoFinanceiro.Entrada
            ? RedirectToAction(nameof(ContasReceber))
            : RedirectToAction(nameof(ContasPagar));
    }

    [HttpPost]
    public async Task<IActionResult> MarcarPago(int id)
    {
        var f = await db.Financeiros.FindAsync(id);
        if (f == null) return NotFound();
        f.Pago = !f.Pago;
        f.DataPagamento = f.Pago ? DateTime.UtcNow : null;
        await db.SaveChangesAsync();
        return Ok(new { pago = f.Pago });
    }

    [HttpPost]
    public async Task<IActionResult> Excluir(int id)
    {
        var f = await db.Financeiros.FindAsync(id);
        if (f == null) return NotFound();
        db.Financeiros.Remove(f);
        await db.SaveChangesAsync();
        return Ok();
    }
}
