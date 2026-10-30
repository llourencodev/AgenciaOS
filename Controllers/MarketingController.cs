using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing;
using AgenciaOS.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>Central de Inteligência de Marketing: dashboards, conteúdo, insights, relatórios e carteira da agência.</summary>
[Authorize]
public class MarketingController(
    ApplicationDbContext db,
    UserManager<Usuario> userManager,
    AcessoMarketingService acesso,
    PainelMarketingService painel,
    IGeradorInsights insights,
    RelatorioMarketingPdfService pdf) : MarketingControllerBase(userManager, acesso)
{
    private static readonly string[] Ordens = ["recentes", "visualizacoes", "interacoes", "alcance"];

    /// <summary>
    /// Valida os filtros contra o isolamento: cliente só vê o próprio ID; agência só IDs existentes.
    /// Retorna false quando o parâmetro aponta para um cliente que o usuário não pode ver.
    /// </summary>
    private async Task<bool> NormalizarFiltroAsync(AcessoMarketingService.Contexto ctx, FiltroMarketing f)
    {
        if (!ctx.EhAgencia)
        {
            if (f.ClienteId != null && f.ClienteId != ctx.ClienteProprioId) return false;
            f.ClienteId = ctx.ClienteProprioId;
        }
        else if (f.ClienteId is int id && !await Acesso.Clientes(ctx).AnyAsync(c => c.Id == id)) return false;

        if (f.ContaId is int contaId && !await Acesso.Contas(ctx).AnyAsync(c => c.Id == contaId && (f.ClienteId == null || c.ClienteId == f.ClienteId)))
            return false;
        if (f.Plataforma is PlataformaMarketing p && !Enum.IsDefined(p)) f.Plataforma = null;
        return true;
    }

    // Visão Geral de Marketing (com abas por plataforma)
    public async Task<IActionResult> Index(FiltroMarketing f)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true } || !await NormalizarFiltroAsync(ctx, f)) return Forbid();
        return View(await painel.MontarAsync(ctx, f));
    }

    // Desempenho de Conteúdo
    public async Task<IActionResult> Conteudo(FiltroMarketing f)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true } || !await NormalizarFiltroAsync(ctx, f)) return Forbid();

        var periodo = PeriodoMarketing.Criar(f.Periodo, f.De, f.Ate, DateOnly.FromDateTime(DateTime.UtcNow));
        var contas = await painel.ContasAsync(ctx, f);
        var ids = contas.Select(c => c.Id).ToList();
        var ini = periodo.Inicio.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var fim = periodo.Fim.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var q = db.PublicacoesMarketing.Where(p => ids.Contains(p.ContaMarketingId) && p.PublicadoEm >= ini && p.PublicadoEm < fim);
        var tipos = await q.Where(p => p.Tipo != null).Select(p => p.Tipo!).Distinct().OrderBy(t => t).ToListAsync();
        if (!string.IsNullOrEmpty(f.Tipo)) q = q.Where(p => p.Tipo == f.Tipo);

        f.Ordem = Ordens.Contains(f.Ordem) ? f.Ordem : "recentes";
        q = f.Ordem switch
        {
            "visualizacoes" => q.OrderByDescending(p => p.Visualizacoes ?? -1).ThenByDescending(p => p.PublicadoEm),
            "interacoes" => q.OrderByDescending(p => p.Interacoes ?? -1).ThenByDescending(p => p.PublicadoEm),
            "alcance" => q.OrderByDescending(p => p.Alcance ?? -1).ThenByDescending(p => p.PublicadoEm),
            _ => q.OrderByDescending(p => p.PublicadoEm)
        };

        const int tamanho = 24;
        f.Pagina = Math.Max(1, f.Pagina);
        var total = await q.CountAsync();
        var lista = await q.Skip((f.Pagina - 1) * tamanho).Take(tamanho).ToListAsync();

        // Seguidores atuais por conta (para engajamento por seguidores quando não há alcance)
        var seguidores = await db.MetricasDiarias
            .Where(m => ids.Contains(m.ContaMarketingId) && m.Metrica == Metricas.Seguidores)
            .GroupBy(m => m.ContaMarketingId)
            .Select(g => new { Conta = g.Key, Valor = g.OrderByDescending(x => x.Data).First().Valor })
            .ToDictionaryAsync(x => x.Conta, x => x.Valor);

        return View(new ConteudoMarketingViewModel
        {
            Filtro = f, Periodo = periodo, EhAgencia = ctx.EhAgencia,
            Clientes = await Acesso.Clientes(ctx).OrderBy(c => c.NomeEmpresa).ToListAsync(),
            Contas = contas, Publicacoes = lista, Total = total, TamanhoPagina = tamanho, Tipos = tipos,
            Melhores = await painel.MelhoresPublicacoesAsync(ids, periodo, 4),
            SeguidoresPorConta = seguidores
        });
    }

    // Insights de Marketing
    public async Task<IActionResult> Insights(FiltroMarketing f)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true } || !await NormalizarFiltroAsync(ctx, f)) return Forbid();
        return View(new InsightsViewModel
        {
            Filtro = f,
            Periodo = PeriodoMarketing.Criar(f.Periodo, f.De, f.Ate, DateOnly.FromDateTime(DateTime.UtcNow)),
            EhAgencia = ctx.EhAgencia,
            Clientes = await Acesso.Clientes(ctx).OrderBy(c => c.NomeEmpresa).ToListAsync(),
            Insights = await insights.GerarAsync(ctx, f)
        });
    }

    // Carteira da agência: todos os clientes, integrações com problema e histórico de sincronizações
    public async Task<IActionResult> Agencia(string? busca, string? situacao, PlataformaMarketing? plataforma)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true, EhAgencia: true }) return Forbid();

        var clientesQ = Acesso.Clientes(ctx);
        if (!string.IsNullOrWhiteSpace(busca))
        {
            var termo = busca.Trim().ToLower();
            clientesQ = clientesQ.Where(c => c.NomeEmpresa.ToLower().Contains(termo) || c.Responsavel.ToLower().Contains(termo));
        }
        var clientes = await clientesQ.OrderBy(c => c.NomeEmpresa).ToListAsync();
        var contas = await painel.ContasAsync(ctx, new FiltroMarketing { Plataforma = plataforma }, somenteComDados: false);

        var periodo = PeriodoMarketing.Criar("30d", null, null, DateOnly.FromDateTime(DateTime.UtcNow));
        var recorte = await painel.CarregarAsync(contas.Where(c => c.Ativa).ToList(), periodo);

        var lista = clientes.Select(c =>
        {
            var doCliente = contas.Where(x => x.ClienteId == c.Id).ToList();
            var ativas = doCliente.Where(x => x.Ativa).ToList();
            decimal? Soma(IEnumerable<decimal?> v) { var l = v.Where(x => x != null).ToList(); return l.Count == 0 ? null : l.Sum(); }
            return new ClienteCarteira
            {
                Cliente = c, Contas = doCliente,
                Seguidores = Soma(ativas.Where(x => PlataformaInfo.Sociais.Contains(x.Plataforma)).Select(x => recorte.SnapshotEm(x.Id, Metricas.Seguidores, periodo.Fim))),
                Interacoes = Soma(ativas.Select(x => recorte.Soma(x.Id, Metricas.Interacoes, true))),
                Sessoes = Soma(ativas.Select(x => recorte.Soma(x.Id, Metricas.Sessoes, true))),
                CliquesOrganicos = Soma(ativas.Select(x => recorte.Soma(x.Id, Metricas.CliquesOrganicos, true))),
            };
        }).ToList();

        lista = situacao switch
        {
            "problemas" => lista.Where(c => c.Problemas > 0).ToList(),
            "expirados" => lista.Where(c => c.Contas.Any(x => x.Ativa && x.Status is StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada)).ToList(),
            "sem_conexao" => lista.Where(c => !c.Contas.Any(x => x.Ativa)).ToList(),
            "ok" => lista.Where(c => c.Contas.Any(x => x.Ativa) && c.Problemas == 0).ToList(),
            _ => lista
        };
        if (plataforma != null) lista = situacao == "sem_conexao" ? lista : lista.Where(c => c.Contas.Any()).ToList();

        var idsClientes = lista.Select(c => c.Cliente.Id).ToList();
        var historico = await db.HistoricosSincronizacao
            .Where(h => idsClientes.Contains(h.ClienteId) && (plataforma == null || h.Plataforma == plataforma))
            .Include(h => h.Conta)
            .OrderByDescending(h => h.IniciadoEm).Take(100).ToListAsync();

        return View(new CarteiraViewModel
        {
            Busca = busca, Situacao = situacao, Plataforma = plataforma, Clientes = lista, Historico = historico,
            NomesClientes = clientes.ToDictionary(c => c.Id, c => c.NomeEmpresa)
        });
    }

    // Relatórios
    public async Task<IActionResult> Relatorios()
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true }) return Forbid();
        var clientes = await Acesso.Clientes(ctx).OrderBy(c => c.NomeEmpresa).ToListAsync();
        var ids = clientes.Select(c => c.Id).ToList();
        return View(new RelatoriosViewModel
        {
            EhAgencia = ctx.EhAgencia, Clientes = clientes,
            Gerados = await db.RelatoriosMarketing.Include(r => r.Cliente).Include(r => r.GeradoPor)
                .Where(r => ids.Contains(r.ClienteId)).OrderByDescending(r => r.GeradoEm).Take(50).ToListAsync()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Relatorios(int clienteId, string periodo, DateOnly? de, DateOnly? ate,
        string[] plataformas, string[] indicadores, bool comparar = true)
    {
        var ctx = await ContextoAsync();
        if (ctx == null || !ctx.PodeAcessarCliente(clienteId)) return Forbid();
        var cliente = await Acesso.Clientes(ctx).FirstOrDefaultAsync(c => c.Id == clienteId);
        if (cliente == null) return Forbid();

        var p = PeriodoMarketing.Criar(periodo, de, ate, DateOnly.FromDateTime(DateTime.UtcNow));
        var validas = plataformas.Where(x => Enum.TryParse<PlataformaMarketing>(x, out var e) && Enum.IsDefined(e)).ToArray();
        if (validas.Length == 0) validas = Enum.GetNames<PlataformaMarketing>();

        var rel = new RelatorioMarketing
        {
            ClienteId = cliente.Id,
            Titulo = $"Relatório de Marketing — {cliente.NomeEmpresa}",
            PeriodoInicio = p.Inicio, PeriodoFim = p.Fim,
            Plataformas = string.Join(",", validas),
            Indicadores = indicadores.Length > 0 ? string.Join(",", indicadores.Where(i => i.Length <= 40)) : null,
            CompararPeriodoAnterior = comparar,
            GeradoPorId = ctx.Usuario.Id
        };
        db.RelatoriosMarketing.Add(rel);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(RelatorioPdf), new { id = rel.Id });
    }

    public async Task<IActionResult> RelatorioPdf(int id)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true }) return Forbid();
        var rel = await db.RelatoriosMarketing.Include(r => r.Cliente).FirstOrDefaultAsync(r => r.Id == id);
        if (rel == null || !ctx.PodeAcessarCliente(rel.ClienteId)) return Forbid();

        var cfg = await db.Configuracoes.OrderBy(c => c.Id).FirstOrDefaultAsync() ?? new Configuracao();
        var bytes = await pdf.GerarAsync(ctx, rel, rel.Cliente!.NomeEmpresa, cfg.CorPrimaria, cfg.NomeAgencia);
        var nome = $"relatorio-marketing-{Slug(rel.Cliente.NomeEmpresa)}-{rel.PeriodoInicio:yyyyMMdd}-{rel.PeriodoFim:yyyyMMdd}.pdf";
        return File(bytes, "application/pdf", nome);
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim().Replace(' ', '-');
}
