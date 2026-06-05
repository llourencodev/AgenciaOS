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
        ViewData["Usuarios"] = await userManager.Users.Where(u => u.Ativo).OrderBy(u => u.Nome).ToListAsync();
    }

    public async Task<IActionResult> Index(int? mes, int? ano)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;
        await CarregarViewData(mes.Value, ano.Value);
        return View();
    }

    public async Task<IActionResult> ContasReceber(int? mes, int? ano, bool? vencidas, int? clienteId, string? responsavelId)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;

        var query = db.Financeiros.Include(f => f.Cliente).Include(f => f.Responsavel)
            .Where(f => f.Tipo == TipoFinanceiro.Entrada
                     && f.DataVencimento.Month == mes && f.DataVencimento.Year == ano)
            .AsQueryable();

        if (vencidas == true)           query = query.Where(f => !f.Pago && f.DataVencimento < DateTime.Now);
        if (clienteId.HasValue)         query = query.Where(f => f.ClienteId == clienteId);
        if (!string.IsNullOrEmpty(responsavelId)) query = query.Where(f => f.ResponsavelId == responsavelId);

        var lista = await query.OrderBy(f => f.DataVencimento).ToListAsync();
        await CarregarViewData(mes.Value, ano.Value);

        ViewData["Vencidas"]      = vencidas;
        ViewData["ClienteId"]     = clienteId;
        ViewData["ResponsavelId"] = responsavelId;
        return View(lista);
    }

    public async Task<IActionResult> ContasPagar(int? mes, int? ano, bool? vencidas, CategoriaFinanceiro? categoria, string? responsavelId, bool? fixas)
    {
        mes ??= DateTime.Now.Month;
        ano ??= DateTime.Now.Year;

        var query = db.Financeiros.Include(f => f.Cliente).Include(f => f.Responsavel)
            .Where(f => f.Tipo == TipoFinanceiro.Saida
                     && f.DataVencimento.Month == mes && f.DataVencimento.Year == ano)
            .AsQueryable();

        if (vencidas == true)             query = query.Where(f => !f.Pago && f.DataVencimento < DateTime.Now);
        if (categoria.HasValue)           query = query.Where(f => f.Categoria == categoria);
        if (!string.IsNullOrEmpty(responsavelId)) query = query.Where(f => f.ResponsavelId == responsavelId);
        if (fixas == true)                query = query.Where(f => f.ContaFixa);

        var lista = await query.OrderBy(f => f.DataVencimento).ToListAsync();
        await CarregarViewData(mes.Value, ano.Value);

        ViewData["Vencidas"]      = vencidas;
        ViewData["Categoria"]     = categoria;
        ViewData["ResponsavelId"] = responsavelId;
        ViewData["Fixas"]         = fixas;
        return View(lista);
    }

    [HttpGet]
    public IActionResult Create() => RedirectToAction(nameof(ContasReceber));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Financeiro model, int? numeroParcelas, bool contaFixa)
    {
        if (!ModelState.IsValid)
            return model.Tipo == TipoFinanceiro.Entrada
                ? RedirectToAction(nameof(ContasReceber))
                : RedirectToAction(nameof(ContasPagar));

        var usuario = await userManager.GetUserAsync(User);
        model.CriadoPorId = usuario?.Id;
        model.CriadoEm = DateTime.UtcNow;

        if (model.Parcelado && numeroParcelas is > 1)
        {
            var grupo = Guid.NewGuid();
            model.GrupoParcelamentoId = grupo;
            model.NumeroParcelas = numeroParcelas;
            var valorParcela = Math.Round(model.Valor / numeroParcelas.Value, 2);
            var dataBase = model.DataVencimento;

            for (int i = 1; i <= numeroParcelas.Value; i++)
            {
                var parcela = new Financeiro
                {
                    Descricao = $"{model.Descricao} ({i}/{numeroParcelas})",
                    Valor = i == numeroParcelas.Value
                        ? model.Valor - (valorParcela * (numeroParcelas.Value - 1))
                        : valorParcela,
                    Tipo = model.Tipo,
                    Categoria = model.Categoria,
                    DataVencimento = dataBase.AddMonths(i - 1),
                    Observacoes = model.Observacoes,
                    ClienteId = model.ClienteId,
                    ResponsavelId = model.ResponsavelId,
                    CriadoPorId = model.CriadoPorId,
                    CriadoEm = DateTime.UtcNow,
                    Parcelado = true,
                    NumeroParcelas = numeroParcelas,
                    ParcelaAtual = i,
                    GrupoParcelamentoId = grupo
                };
                db.Financeiros.Add(parcela);
            }
        }
        else if (contaFixa)
        {
            var dataBase = model.DataVencimento;
            var grupo = Guid.NewGuid();
            int mesesRestantes = 12 - dataBase.Month + 1;

            for (int i = 0; i < mesesRestantes; i++)
            {
                var fixa = new Financeiro
                {
                    Descricao = model.Descricao,
                    Valor = model.Valor,
                    Tipo = model.Tipo,
                    Categoria = model.Categoria,
                    DataVencimento = dataBase.AddMonths(i),
                    Observacoes = model.Observacoes,
                    ClienteId = model.ClienteId,
                    ResponsavelId = model.ResponsavelId,
                    CriadoPorId = model.CriadoPorId,
                    CriadoEm = DateTime.UtcNow,
                    ContaFixa = true,
                    GrupoParcelamentoId = grupo
                };
                db.Financeiros.Add(fixa);
            }
        }
        else
        {
            db.Financeiros.Add(model);
        }

        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Lançamento criado com sucesso!";

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

    public async Task<IActionResult> Relatorio(int? ano)
    {
        ano ??= DateTime.Now.Year;
        var todos = await db.Financeiros
            .Include(f => f.Cliente)
            .Where(f => f.DataVencimento.Year == ano)
            .ToListAsync();

        var cultura = new System.Globalization.CultureInfo("pt-BR");

        var porMes = Enumerable.Range(1, 12).Select(m => new
        {
            Mes = m,
            NomeMes = new DateTime(ano.Value, m, 1).ToString("MMM", cultura),
            Receita = todos.Where(f => f.DataVencimento.Month == m && f.Tipo == TipoFinanceiro.Entrada && f.Pago).Sum(f => f.Valor),
            Despesa = todos.Where(f => f.DataVencimento.Month == m && f.Tipo == TipoFinanceiro.Saida && f.Pago).Sum(f => f.Valor),
            Pendente = todos.Where(f => f.DataVencimento.Month == m && !f.Pago).Sum(f => f.Valor),
        }).ToList();

        var porCliente = todos
            .Where(f => f.Cliente != null && f.Tipo == TipoFinanceiro.Entrada && f.Pago)
            .GroupBy(f => f.Cliente!.NomeEmpresa)
            .Select(g => new { Cliente = g.Key, Total = g.Sum(f => f.Valor) })
            .OrderByDescending(g => g.Total)
            .Take(10)
            .ToList();

        var porCategoria = todos
            .GroupBy(f => f.Categoria)
            .Select(g => new { Categoria = g.Key.ToString(), Total = g.Sum(f => f.Valor), Tipo = g.First().Tipo })
            .OrderByDescending(g => g.Total)
            .ToList();

        ViewData["Ano"]          = ano;
        ViewData["PorMes"]       = porMes;
        ViewData["PorCliente"]   = porCliente;
        ViewData["PorCategoria"] = porCategoria;
        ViewData["TotalReceita"] = todos.Where(f => f.Tipo == TipoFinanceiro.Entrada && f.Pago).Sum(f => f.Valor);
        ViewData["TotalDespesa"] = todos.Where(f => f.Tipo == TipoFinanceiro.Saida && f.Pago).Sum(f => f.Valor);
        ViewData["TotalLucro"]   = todos.Where(f => f.Tipo == TipoFinanceiro.Entrada && f.Pago).Sum(f => f.Valor)
                                  - todos.Where(f => f.Tipo == TipoFinanceiro.Saida && f.Pago).Sum(f => f.Valor);

        return View();
    }
}
