using System.Globalization;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing.Provedores;
using AgenciaOS.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Monta indicadores, gráficos e tabelas a partir do histórico gravado no banco.
/// Nenhuma chamada externa acontece aqui.
/// </summary>
public class PainelMarketingService(ApplicationDbContext db, AcessoMarketingService acesso)
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    /// <summary>Dados brutos de um recorte (contas × período atual + anterior), carregados uma vez.</summary>
    public class Recorte
    {
        public required PeriodoMarketing Atual { get; init; }
        public required PeriodoMarketing Anterior { get; init; }
        public required List<ContaResumo> Contas { get; init; }
        /// <summary>Somas por (conta, métrica, período atual?)</summary>
        public Dictionary<(int Conta, string Metrica, bool Atual), decimal> Somas { get; init; } = [];
        /// <summary>Série diária por (plataforma, métrica) somando as contas.</summary>
        public Dictionary<(PlataformaMarketing, string), Dictionary<DateOnly, decimal>> Series { get; init; } = [];
        /// <summary>Snapshots por (conta, métrica) ordenados por data.</summary>
        public Dictionary<(int Conta, string Metrica), SortedList<DateOnly, decimal>> Snapshots { get; init; } = [];

        public IEnumerable<ContaResumo> DaPlataforma(params PlataformaMarketing[] p) => Contas.Where(c => p.Contains(c.Plataforma));

        public decimal? Soma(int conta, string metrica, bool atual) =>
            Somas.TryGetValue((conta, metrica, atual), out var v) ? v : null;

        public decimal? SnapshotEm(int conta, string metrica, DateOnly data)
        {
            if (!Snapshots.TryGetValue((conta, metrica), out var lista)) return null;
            decimal? valor = null;
            foreach (var (d, v) in lista) { if (d > data) break; valor = v; }
            return valor;
        }

        public bool Indisponivel(ContaResumo c, string metrica) => c.Indisponiveis.Contains(metrica);
    }

    // ══════════════════════════════════════════════════════════════
    //  Carga de dados
    // ══════════════════════════════════════════════════════════════

    public async Task<List<ContaResumo>> ContasAsync(AcessoMarketingService.Contexto ctx, FiltroMarketing f, bool somenteComDados = true)
    {
        var q = acesso.Contas(ctx).Include(c => c.Cliente).AsQueryable();
        if (f.ClienteId is int cid) q = q.Where(c => c.ClienteId == cid);
        if (f.Plataforma is PlataformaMarketing p) q = q.Where(c => c.Plataforma == p);
        if (f.ContaId is int id) q = q.Where(c => c.Id == id);
        if (somenteComDados) q = q.Where(c => c.Ativa || c.UltimaSincronizacao != null);

        return await q.OrderBy(c => c.Cliente!.NomeEmpresa).ThenBy(c => c.Plataforma).ThenBy(c => c.Nome)
            .Select(c => new ContaResumo
            {
                Id = c.Id, ClienteId = c.ClienteId, Cliente = c.Cliente!.NomeEmpresa, Plataforma = c.Plataforma,
                Nome = c.Nome, NomeUsuario = c.NomeUsuario, FotoUrl = c.FotoUrl, Status = c.Status, Ativa = c.Ativa,
                UltimaSincronizacao = c.UltimaSincronizacao, UltimoErro = c.UltimoErro, Limitacao = c.Limitacao,
                Indisponiveis = c.MetricasIndisponiveis == null ? Array.Empty<string>() : c.MetricasIndisponiveis.Split(',', StringSplitOptions.None)
            }).ToListAsync();
    }

    public async Task<Recorte> CarregarAsync(List<ContaResumo> contas, PeriodoMarketing atual)
    {
        var anterior = atual.Anterior();
        var ids = contas.Select(c => c.Id).ToList();
        var snapshotKeys = Metricas.Definicoes.Values.Where(d => d.Agregacao == AgregacaoMetrica.Ultimo).Select(d => d.Chave).ToList();
        var recorte = new Recorte { Atual = atual, Anterior = anterior, Contas = contas };
        if (ids.Count == 0) return recorte;

        var (ini, fim, iniAtual) = (anterior.Inicio, atual.Fim, atual.Inicio);

        var somas = await db.MetricasDiarias
            .Where(m => ids.Contains(m.ContaMarketingId) && m.Data >= ini && m.Data <= fim && !snapshotKeys.Contains(m.Metrica))
            .GroupBy(m => new { m.ContaMarketingId, m.Metrica, Atual = m.Data >= iniAtual })
            .Select(g => new { g.Key.ContaMarketingId, g.Key.Metrica, g.Key.Atual, Total = g.Sum(x => x.Valor) })
            .ToListAsync();
        foreach (var s in somas) recorte.Somas[(s.ContaMarketingId, s.Metrica, s.Atual)] = s.Total;

        var series = await db.MetricasDiarias
            .Where(m => ids.Contains(m.ContaMarketingId) && m.Data >= iniAtual && m.Data <= fim && !snapshotKeys.Contains(m.Metrica))
            .GroupBy(m => new { m.Plataforma, m.Metrica, m.Data })
            .Select(g => new { g.Key.Plataforma, g.Key.Metrica, g.Key.Data, Total = g.Sum(x => x.Valor) })
            .ToListAsync();
        foreach (var s in series)
        {
            if (!recorte.Series.TryGetValue((s.Plataforma, s.Metrica), out var dic))
                recorte.Series[(s.Plataforma, s.Metrica)] = dic = [];
            dic[s.Data] = s.Total;
        }

        // Snapshots: inclui uma folga de 60 dias antes para achar o último valor conhecido
        var iniSnap = ini.AddDays(-60);
        var snaps = await db.MetricasDiarias
            .Where(m => ids.Contains(m.ContaMarketingId) && m.Data >= iniSnap && m.Data <= fim && snapshotKeys.Contains(m.Metrica))
            .Select(m => new { m.ContaMarketingId, m.Metrica, m.Data, m.Valor })
            .ToListAsync();
        foreach (var s in snaps)
        {
            if (!recorte.Snapshots.TryGetValue((s.ContaMarketingId, s.Metrica), out var lista))
                recorte.Snapshots[(s.ContaMarketingId, s.Metrica)] = lista = [];
            lista[s.Data] = s.Valor;
        }
        return recorte;
    }

    // ══════════════════════════════════════════════════════════════
    //  Indicadores
    // ══════════════════════════════════════════════════════════════

    private static string RotuloConta(ContaResumo c) =>
        $"{PlataformaInfo.Nome(c.Plataforma)} · {(c.NomeUsuario != null ? "@" + c.NomeUsuario : c.Nome)}";

    /// <summary>Indicador de soma. Sem total quando as fontes são de plataformas diferentes e o conceito não é comparável.</summary>
    public KpiMarketing KpiSoma(Recorte r, string chave, string titulo, string icone, string cor, string metrica,
        PlataformaMarketing[] plataformas, bool permiteTotalEntrePlataformas, string? metodologia = null, FormatoKpi formato = FormatoKpi.Numero)
    {
        var kpi = new KpiMarketing { Chave = chave, Titulo = titulo, Icone = icone, Cor = cor, Formato = formato, Metodologia = metodologia };
        var indisponiveis = new List<string>();
        foreach (var c in r.DaPlataforma(plataformas))
        {
            var atual = r.Soma(c.Id, metrica, true);
            if (atual is null && r.Indisponivel(c, metrica)) { indisponiveis.Add(RotuloConta(c)); continue; }
            if (atual is null) continue; // sem dados desta métrica no período
            kpi.Fontes.Add(new FonteKpi { Plataforma = c.Plataforma, Conta = RotuloConta(c), Valor = atual, Anterior = r.Soma(c.Id, metrica, false) });
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        FecharTotal(kpi, permiteTotalEntrePlataformas);
        if (kpi.Fontes.Count == 0 && indisponiveis.Count > 0)
            kpi.Indisponivel = $"Não disponibilizado pela API para: {string.Join(", ", indisponiveis)}.";
        return kpi;
    }

    private static void FecharTotal(KpiMarketing kpi, bool permiteTotalEntrePlataformas)
    {
        if (kpi.Fontes.Count == 0) return;
        var plataformasDistintas = kpi.Fontes.Select(f => f.Plataforma).Distinct().Count();
        if (plataformasDistintas > 1 && !permiteTotalEntrePlataformas)
        {
            kpi.SemTotal = "Sem total: esta métrica tem definições diferentes em cada plataforma e não é somada. Veja os valores por fonte.";
            return;
        }
        kpi.Valor = kpi.Fontes.Sum(f => f.Valor ?? 0);
        kpi.Anterior = kpi.Fontes.All(f => f.Anterior is null) ? null : kpi.Fontes.Sum(f => f.Anterior ?? 0);
    }

    public KpiMarketing KpiSeguidores(Recorte r, PlataformaMarketing[] plataformas)
    {
        var kpi = new KpiMarketing
        {
            Chave = "seguidores", Titulo = "Seguidores totais", Icone = "bi-people-fill", Cor = "purple",
            Metodologia = "Último valor conhecido de cada perfil no fim do período. Na soma entre redes, a mesma pessoa pode seguir mais de um perfil."
        };
        foreach (var c in r.DaPlataforma(plataformas))
        {
            var atual = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Atual.Fim);
            if (atual is null) continue;
            kpi.Fontes.Add(new FonteKpi { Plataforma = c.Plataforma, Conta = RotuloConta(c), Valor = atual, Anterior = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Anterior.Fim) });
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        FecharTotal(kpi, true);
        return kpi;
    }

    /// <summary>
    /// Crescimento líquido: seguidores no fim − seguidores antes do início (snapshots).
    /// Sem snapshot no início, usa "novos seguidores" informados pela API (crescimento bruto), indicando a metodologia.
    /// </summary>
    public KpiMarketing KpiCrescimento(Recorte r, PlataformaMarketing[] plataformas)
    {
        var kpi = new KpiMarketing
        {
            Chave = "crescimento", Titulo = "Crescimento de seguidores", Icone = "bi-graph-up-arrow", Cor = "green",
            Metodologia = "Diferença entre os seguidores no fim e no início do período (histórico sincronizado). A taxa = ((atuais − anteriores) / anteriores) × 100."
        };
        var usouBruto = false;
        foreach (var c in r.DaPlataforma(plataformas))
        {
            var fim = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Atual.Fim);
            var inicio = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Atual.Inicio.AddDays(-1));
            var fimAnt = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Anterior.Fim);
            var iniAnt = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Anterior.Inicio.AddDays(-1));
            decimal? valor = fim is not null && inicio is not null ? fim - inicio : null;
            decimal? anterior = fimAnt is not null && iniAnt is not null ? fimAnt - iniAnt : null;
            string? obs = null;
            if (valor is null && r.Soma(c.Id, Metricas.NovosSeguidores, true) is decimal novos)
            {
                valor = novos;
                anterior = r.Soma(c.Id, Metricas.NovosSeguidores, false);
                obs = "novos seguidores informados pela API (não desconta quem deixou de seguir)";
                usouBruto = true;
            }
            if (valor is null) continue;
            var taxa = CalculosMarketing.CrescimentoSeguidores(fim, inicio);
            kpi.Fontes.Add(new FonteKpi
            {
                Plataforma = c.Plataforma, Conta = RotuloConta(c), Valor = valor, Anterior = anterior,
                Observacao = obs ?? (taxa is decimal t ? $"taxa de crescimento {t.ToString("0.##", PtBr)}%" : null)
            });
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        FecharTotal(kpi, true);
        if (kpi.Fontes.Count == 0)
            kpi.Indisponivel = "Histórico insuficiente: o crescimento aparece quando houver sincronizações no início e no fim do período.";
        if (usouBruto) kpi.Metodologia += " Onde não há histórico no início do período, usa novos seguidores informados pela API.";
        return kpi;
    }

    /// <summary>Taxa de engajamento por seguidores = (interações / seguidores) × 100.</summary>
    public KpiMarketing KpiEngajamento(Recorte r, PlataformaMarketing[] plataformas)
    {
        var kpi = new KpiMarketing
        {
            Chave = "engajamento", Titulo = "Engajamento por seguidores", Icone = "bi-heart-pulse-fill", Cor = "orange", Formato = FormatoKpi.Percentual,
            Metodologia = "(interações no período / seguidores no fim do período) × 100. Não é o mesmo que engajamento por alcance."
        };
        decimal somaI = 0, somaS = 0, somaIa = 0, somaSa = 0;
        foreach (var c in r.DaPlataforma(plataformas))
        {
            var inter = r.Soma(c.Id, Metricas.Interacoes, true);
            var seg = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Atual.Fim);
            var taxa = CalculosMarketing.EngajamentoPorSeguidores(inter, seg);
            if (taxa is null) continue;
            var interA = r.Soma(c.Id, Metricas.Interacoes, false);
            var segA = r.SnapshotEm(c.Id, Metricas.Seguidores, r.Anterior.Fim);
            kpi.Fontes.Add(new FonteKpi { Plataforma = c.Plataforma, Conta = RotuloConta(c), Valor = taxa, Anterior = CalculosMarketing.EngajamentoPorSeguidores(interA, segA) });
            somaI += inter!.Value; somaS += seg!.Value;
            if (interA is not null && segA is not null) { somaIa += interA.Value; somaSa += segA.Value; }
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        if (kpi.Fontes.Count > 0)
        {
            kpi.Valor = CalculosMarketing.EngajamentoPorSeguidores(somaI, somaS);
            kpi.Anterior = CalculosMarketing.EngajamentoPorSeguidores(somaIa, somaSa);
        }
        else kpi.Indisponivel = "Requer interações e seguidores sincronizados na mesma conta.";
        return kpi;
    }

    /// <summary>Engajamento por alcance = (interações / alcance) × 100, por conta.</summary>
    public KpiMarketing KpiEngajamentoAlcance(Recorte r, PlataformaMarketing plataforma)
    {
        var kpi = new KpiMarketing
        {
            Chave = "engajamento_alcance", Titulo = "Engajamento por alcance", Icone = "bi-bullseye", Cor = "red", Formato = FormatoKpi.Percentual,
            Metodologia = "(interações / soma do alcance diário) × 100. Como o alcance diário somado conta a mesma pessoa em dias diferentes, a taxa tende a ser conservadora."
        };
        decimal i = 0, a = 0, ia = 0, aa = 0;
        foreach (var c in r.DaPlataforma(plataforma))
        {
            if (r.Soma(c.Id, Metricas.Interacoes, true) is not decimal inter || r.Soma(c.Id, Metricas.Alcance, true) is not decimal alc || alc == 0) continue;
            i += inter; a += alc;
            ia += r.Soma(c.Id, Metricas.Interacoes, false) ?? 0; aa += r.Soma(c.Id, Metricas.Alcance, false) ?? 0;
            kpi.Fontes.Add(new FonteKpi { Plataforma = c.Plataforma, Conta = RotuloConta(c), Valor = CalculosMarketing.EngajamentoPorAlcance(inter, alc) });
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        kpi.Valor = CalculosMarketing.EngajamentoPorAlcance(i, a);
        kpi.Anterior = CalculosMarketing.EngajamentoPorAlcance(ia, aa);
        if (kpi.Fontes.Count == 0) kpi.Indisponivel = "Requer interações e alcance disponibilizados pela API.";
        return kpi;
    }

    /// <summary>Indicador derivado de duas somas (razão), ex.: CTR, taxa de engajamento do site, posição média.</summary>
    public KpiMarketing KpiRazao(Recorte r, string chave, string titulo, string icone, string cor, PlataformaMarketing plataforma,
        string numerador, string denominador, decimal multiplicador, FormatoKpi formato, string metodologia)
    {
        var kpi = new KpiMarketing { Chave = chave, Titulo = titulo, Icone = icone, Cor = cor, Formato = formato, Metodologia = metodologia };
        decimal n = 0, d = 0, na = 0, da = 0;
        var tem = false;
        foreach (var c in r.DaPlataforma(plataforma))
        {
            if (r.Soma(c.Id, numerador, true) is not decimal x || r.Soma(c.Id, denominador, true) is not decimal y) continue;
            tem = true; n += x; d += y;
            na += r.Soma(c.Id, numerador, false) ?? 0; da += r.Soma(c.Id, denominador, false) ?? 0;
            kpi.Fontes.Add(new FonteKpi { Plataforma = c.Plataforma, Conta = RotuloConta(c), Valor = CalculosMarketing.Divisao(x, y) * multiplicador });
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        if (!tem) { kpi.Indisponivel = "Sem dados sincronizados para o cálculo."; return kpi; }
        kpi.Valor = CalculosMarketing.Divisao(n, d) is decimal v ? Math.Round(v * multiplicador, 2) : null;
        kpi.Anterior = CalculosMarketing.Divisao(na, da) is decimal va ? Math.Round(va * multiplicador, 2) : null;
        return kpi;
    }

    // ══════════════════════════════════════════════════════════════
    //  Montagem das abas
    // ══════════════════════════════════════════════════════════════

    public async Task<PainelMarketingViewModel> MontarAsync(AcessoMarketingService.Contexto ctx, FiltroMarketing f)
    {
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var periodo = PeriodoMarketing.Criar(f.Periodo, f.De, f.Ate, hoje);
        f.Aba ??= f.Plataforma switch
        {
            PlataformaMarketing.GoogleAnalytics => "website",
            PlataformaMarketing.SearchConsole => "seo",
            PlataformaMarketing p => PlataformaInfo.Slug(p),
            _ => "geral"
        };

        var aba = f.Aba;
        var plataformaAba = aba switch
        {
            "instagram" => PlataformaMarketing.Instagram,
            "facebook" => PlataformaMarketing.Facebook,
            "tiktok" => PlataformaMarketing.TikTok,
            "website" => PlataformaMarketing.GoogleAnalytics,
            "seo" => PlataformaMarketing.SearchConsole,
            _ => (PlataformaMarketing?)null
        };
        var filtroContas = new FiltroMarketing { ClienteId = f.ClienteId, ContaId = f.ContaId, Plataforma = plataformaAba ?? f.Plataforma };
        var contas = await ContasAsync(ctx, filtroContas);
        var r = await CarregarAsync(contas, periodo);

        var vm = new PainelMarketingViewModel
        {
            Filtro = f, Periodo = periodo, PeriodoAnterior = r.Anterior, EhAgencia = ctx.EhAgencia,
            Clientes = await acesso.Clientes(ctx).OrderBy(c => c.NomeEmpresa).ToListAsync(),
            Contas = contas
        };
        if (periodo.Codigo == "hoje")
            vm.Avisos.Add("\"Hoje\": as plataformas consolidam os números do dia com atraso; redes sociais e Search Console podem não ter dados do dia corrente.");
        foreach (var c in contas.Where(c => c.Limitacao != null))
            vm.Avisos.Add($"{RotuloConta(c)}: {c.Limitacao}");

        switch (aba)
        {
            case "instagram": case "facebook": case "tiktok":
                MontarSocial(vm, r, plataformaAba!.Value);
                break;
            case "website":
                MontarWebsite(vm, r);
                vm.Tabelas.AddRange(await TabelasWebsiteAsync(contas, periodo));
                break;
            case "seo":
                MontarSeo(vm, r);
                vm.Tabelas.AddRange(await TabelasSeoAsync(contas, periodo));
                break;
            default:
                MontarGeral(vm, r);
                var canais = await RankingDimensaoAsync(contas.Where(c => c.Plataforma == PlataformaMarketing.GoogleAnalytics), periodo, Dimensoes.Canal, Metricas.Sessoes, 8);
                if (canais.Count > 0)
                    vm.Graficos.Add(new GraficoMarketing
                    {
                        Id = "origem", Titulo = "Origem dos visitantes do site (sessões, GA4)", Tipo = "doughnut",
                        Rotulos = canais.Select(x => x.Valor).ToList(), Series = [new SerieGrafico { Rotulo = "Sessões", Valores = canais.Select(x => (decimal?)x.Total).ToList() }]
                    });
                break;
        }

        vm.MelhoresPublicacoes.AddRange(await MelhoresPublicacoesAsync(contas.Select(c => c.Id).ToList(), periodo, 6));
        if (vm.MelhoresPublicacoes.Count > 0 && aba is "geral" or "instagram" or "facebook" or "tiktok")
            vm.Graficos.Add(new GraficoMarketing
            {
                Id = "posts", Titulo = "Desempenho das publicações do período (interações)", Tipo = "bar",
                Rotulos = vm.MelhoresPublicacoes.Select(p => $"{PlataformaInfo.Nome(p.Plataforma)} {p.PublicadoEm:dd/MM}").ToList(),
                Series = [new SerieGrafico { Rotulo = "Interações", Valores = vm.MelhoresPublicacoes.Select(p => (decimal?)p.Interacoes).ToList() }],
                Nota = "Totais acumulados das publicações feitas no período, no momento da última sincronização."
            });
        return vm;
    }

    private void MontarGeral(PainelMarketingViewModel vm, Recorte r)
    {
        var sociais = PlataformaInfo.Sociais;
        vm.Kpis.Add(KpiSeguidores(r, sociais));
        vm.Kpis.Add(KpiCrescimento(r, sociais));
        vm.Kpis.Add(KpiSoma(r, "alcance", "Alcance", "bi-broadcast", "blue", Metricas.Alcance, sociais, false, Metricas.Definicoes[Metricas.Alcance].Observacao));
        vm.Kpis.Add(KpiSoma(r, "impressoes", "Impressões", "bi-eye-fill", "indigo", Metricas.Impressoes, sociais, false));
        vm.Kpis.Add(KpiSoma(r, "visualizacoes", "Visualizações", "bi-play-circle-fill", "purple", Metricas.Visualizacoes, sociais, false,
            "Visualizações de conteúdo informadas por dia. O TikTok só informa visualizações por vídeo (veja Desempenho de Conteúdo)."));
        vm.Kpis.Add(KpiEngajamento(r, sociais));
        vm.Kpis.Add(KpiSoma(r, "interacoes", "Interações", "bi-hand-thumbs-up-fill", "orange", Metricas.Interacoes, sociais, true,
            "Soma de interações (curtidas, comentários, compartilhamentos, salvamentos) informadas por cada rede. Cada interação é um evento distinto, então a soma é válida."));
        vm.Kpis.Add(KpiSoma(r, "cliques", "Cliques", "bi-cursor-fill", "red", Metricas.Cliques, [PlataformaMarketing.Facebook], false,
            "Cliques em ações da página do Facebook. Toques em links do Instagram e cliques orgânicos do Google aparecem nas abas próprias."));
        vm.Kpis.Add(KpiSoma(r, "visitas_site", "Visitas ao site", "bi-globe2", "green", Metricas.Sessoes, [PlataformaMarketing.GoogleAnalytics], false,
            "Sessões registradas pelo Google Analytics 4."));
        vm.Kpis.Add(KpiSoma(r, "conversoes", "Conversões", "bi-trophy-fill", "indigo", Metricas.EventosPrincipais, [PlataformaMarketing.GoogleAnalytics], false,
            "Eventos principais (key events) configurados no GA4."));

        vm.Graficos.Add(SerieSnapshot(r, "seguidores", "Crescimento de seguidores", Metricas.Seguidores, sociais));
        vm.Graficos.Add(SeriePorPlataforma(r, "alcance", "Alcance por dia", Metricas.Alcance, sociais, "line"));
        vm.Graficos.Add(SeriePorPlataforma(r, "engajamento", "Interações por dia", Metricas.Interacoes, sociais, "bar"));
        vm.Graficos.Add(SeriePorPlataforma(r, "visualizacoes", "Visualizações por dia", Metricas.Visualizacoes, sociais, "line"));
        vm.Graficos.Add(SeriePorPlataforma(r, "trafego", "Tráfego do site (sessões por dia)", Metricas.Sessoes, [PlataformaMarketing.GoogleAnalytics], "line"));
    }

    private void MontarSocial(PainelMarketingViewModel vm, Recorte r, PlataformaMarketing p)
    {
        PlataformaMarketing[] ps = [p];
        vm.Kpis.Add(KpiSeguidores(r, ps));
        vm.Kpis.Add(KpiCrescimento(r, ps));
        if (p == PlataformaMarketing.TikTok)
        {
            vm.Kpis.Add(KpiSnapshot(r, "curtidas_totais", "Curtidas totais do perfil", "bi-heart-fill", "red", Metricas.CurtidasTotais, p));
            vm.Kpis.Add(KpiSnapshot(r, "videos", "Vídeos publicados", "bi-collection-play-fill", "blue", Metricas.Publicacoes, p));
        }
        else
        {
            vm.Kpis.Add(KpiSoma(r, "alcance", "Alcance", "bi-broadcast", "blue", Metricas.Alcance, ps, false, Metricas.Definicoes[Metricas.Alcance].Observacao));
            vm.Kpis.Add(KpiSoma(r, "impressoes", "Impressões", "bi-eye-fill", "indigo", Metricas.Impressoes, ps, false));
            vm.Kpis.Add(KpiSoma(r, "visualizacoes", "Visualizações", "bi-play-circle-fill", "purple", Metricas.Visualizacoes, ps, false));
            vm.Kpis.Add(KpiSoma(r, "interacoes", "Interações", "bi-hand-thumbs-up-fill", "orange", Metricas.Interacoes, ps, true));
            vm.Kpis.Add(KpiEngajamento(r, ps));
            vm.Kpis.Add(KpiEngajamentoAlcance(r, p));
            vm.Kpis.Add(KpiSoma(r, "curtidas", "Curtidas / reações", "bi-heart-fill", "red", Metricas.Curtidas, ps, true));
            if (p == PlataformaMarketing.Instagram)
            {
                vm.Kpis.Add(KpiSoma(r, "comentarios", "Comentários", "bi-chat-fill", "blue", Metricas.Comentarios, ps, true));
                vm.Kpis.Add(KpiSoma(r, "compartilhamentos", "Compartilhamentos", "bi-share-fill", "green", Metricas.Compartilhamentos, ps, true));
                vm.Kpis.Add(KpiSoma(r, "salvamentos", "Salvamentos", "bi-bookmark-fill", "indigo", Metricas.Salvamentos, ps, true));
                vm.Kpis.Add(KpiSoma(r, "contas_engajadas", "Contas engajadas", "bi-person-check-fill", "purple", Metricas.ContasEngajadas, ps, true,
                    "Soma diária de contas que interagiram (a mesma conta em dias diferentes conta mais de uma vez)."));
                vm.Kpis.Add(KpiSoma(r, "cliques_link", "Toques em links do perfil", "bi-link-45deg", "orange", Metricas.CliquesLink, ps, true));
            }
            else
            {
                vm.Kpis.Add(KpiSoma(r, "cliques", "Cliques em ações da página", "bi-cursor-fill", "orange", Metricas.Cliques, ps, true));
                vm.Kpis.Add(KpiSoma(r, "videos", "Visualizações de vídeo", "bi-camera-video-fill", "purple", Metricas.VisualizacoesVideo, ps, true));
            }
            vm.Kpis.Add(KpiSoma(r, "visitas_perfil", "Visitas ao perfil", "bi-person-badge-fill", "green", Metricas.VisitasPerfil, ps, true));
        }

        vm.Graficos.Add(SerieSnapshot(r, "seguidores", "Crescimento de seguidores", Metricas.Seguidores, ps));
        if (p != PlataformaMarketing.TikTok)
        {
            vm.Graficos.Add(SeriePorPlataforma(r, "alcance", "Alcance por dia", Metricas.Alcance, ps, "line"));
            vm.Graficos.Add(SeriePorPlataforma(r, "engajamento", "Interações por dia", Metricas.Interacoes, ps, "bar"));
            vm.Graficos.Add(SeriePorPlataforma(r, "visualizacoes", "Visualizações por dia", Metricas.Visualizacoes, ps, "line"));
        }
    }

    private void MontarWebsite(PainelMarketingViewModel vm, Recorte r)
    {
        var ga = PlataformaMarketing.GoogleAnalytics;
        PlataformaMarketing[] ps = [ga];
        vm.Kpis.Add(KpiSoma(r, "usuarios", "Usuários ativos", "bi-people-fill", "purple", Metricas.UsuariosAtivos, ps, true, Metricas.Definicoes[Metricas.UsuariosAtivos].Observacao));
        vm.Kpis.Add(KpiSoma(r, "novos_usuarios", "Novos usuários", "bi-person-plus-fill", "green", Metricas.NovosUsuarios, ps, true));
        vm.Kpis.Add(KpiSoma(r, "sessoes", "Sessões", "bi-door-open-fill", "blue", Metricas.Sessoes, ps, true));
        vm.Kpis.Add(KpiSoma(r, "sessoes_engajadas", "Sessões engajadas", "bi-lightning-fill", "orange", Metricas.SessoesEngajadas, ps, true));
        vm.Kpis.Add(KpiRazao(r, "taxa_engajamento", "Taxa de engajamento", "bi-percent", "red", ga, Metricas.SessoesEngajadas, Metricas.Sessoes, 100, FormatoKpi.Percentual,
            "(sessões engajadas / sessões) × 100, como no GA4."));
        vm.Kpis.Add(KpiSoma(r, "pageviews", "Visualizações de página", "bi-file-earmark-text-fill", "indigo", Metricas.VisualizacoesPagina, ps, true));
        vm.Kpis.Add(KpiRazao(r, "tempo_medio", "Tempo médio de engajamento", "bi-stopwatch-fill", "blue", ga, Metricas.TempoEngajamento, Metricas.UsuariosAtivos, 1, FormatoKpi.Segundos,
            "Tempo total de engajamento / soma diária de usuários ativos (aproximação do indicador do GA4)."));
        vm.Kpis.Add(KpiSoma(r, "conversoes", "Conversões (eventos principais)", "bi-trophy-fill", "green", Metricas.EventosPrincipais, ps, true));

        vm.Graficos.Add(new GraficoMarketing
        {
            Id = "trafego", Titulo = "Tráfego do site por dia", Tipo = "line", Rotulos = Rotulos(r.Atual),
            Series =
            [
                Serie(r, ga, Metricas.Sessoes, "Sessões", "#3b82f6"),
                Serie(r, ga, Metricas.UsuariosAtivos, "Usuários ativos", "#8b5cf6"),
                Serie(r, ga, Metricas.EventosPrincipais, "Conversões", "#10b981"),
            ]
        });
    }

    private void MontarSeo(PainelMarketingViewModel vm, Recorte r)
    {
        var gsc = PlataformaMarketing.SearchConsole;
        PlataformaMarketing[] ps = [gsc];
        vm.Kpis.Add(KpiSoma(r, "cliques_organicos", "Cliques orgânicos", "bi-cursor-fill", "blue", Metricas.CliquesOrganicos, ps, true));
        vm.Kpis.Add(KpiSoma(r, "impressoes_organicas", "Impressões na pesquisa", "bi-eye-fill", "purple", Metricas.ImpressoesOrganicas, ps, true));
        vm.Kpis.Add(KpiRazao(r, "ctr", "CTR", "bi-percent", "orange", gsc, Metricas.CliquesOrganicos, Metricas.ImpressoesOrganicas, 100, FormatoKpi.Percentual,
            "(cliques / impressões) × 100."));
        vm.Kpis.Add(KpiRazao(r, "posicao", "Posição média", "bi-sort-numeric-up", "green", gsc, Metricas.PosicaoPonderada, Metricas.ImpressoesOrganicas, 1, FormatoKpi.Posicao,
            "Média ponderada pelas impressões, como no Search Console. Quanto menor, melhor."));
        vm.Avisos.Add("Dados do Search Console (pesquisa orgânica do Google) são diferentes dos dados do Google Analytics e não são somados a eles.");

        vm.Graficos.Add(new GraficoMarketing
        {
            Id = "seo", Titulo = "Evolução do tráfego orgânico", Tipo = "line", Rotulos = Rotulos(r.Atual),
            Series =
            [
                Serie(r, gsc, Metricas.CliquesOrganicos, "Cliques", "#3b82f6"),
                new SerieGrafico { Rotulo = "Impressões", Cor = "#a855f7", Eixo = "y2", Valores = Serie(r, gsc, Metricas.ImpressoesOrganicas, "", "").Valores },
            ]
        });
    }

    private KpiMarketing KpiSnapshot(Recorte r, string chave, string titulo, string icone, string cor, string metrica, PlataformaMarketing p)
    {
        var kpi = new KpiMarketing { Chave = chave, Titulo = titulo, Icone = icone, Cor = cor, Metodologia = "Total acumulado do perfil no fim do período." };
        foreach (var c in r.DaPlataforma(p))
        {
            if (r.SnapshotEm(c.Id, metrica, r.Atual.Fim) is not decimal v) continue;
            kpi.Fontes.Add(new FonteKpi { Plataforma = p, Conta = RotuloConta(c), Valor = v, Anterior = r.SnapshotEm(c.Id, metrica, r.Anterior.Fim) });
            kpi.UltimaAtualizacao = Max(kpi.UltimaAtualizacao, c.UltimaSincronizacao);
        }
        FecharTotal(kpi, true);
        return kpi;
    }

    // ══════════════════════════════════════════════════════════════
    //  Séries e tabelas
    // ══════════════════════════════════════════════════════════════

    private static List<string> Rotulos(PeriodoMarketing p) =>
        Enumerable.Range(0, p.Dias).Select(i => p.Inicio.AddDays(i).ToString("dd/MM", PtBr)).ToList();

    private static SerieGrafico Serie(Recorte r, PlataformaMarketing p, string metrica, string rotulo, string cor)
    {
        r.Series.TryGetValue((p, metrica), out var dic);
        return new SerieGrafico
        {
            Rotulo = rotulo, Cor = cor,
            Valores = Enumerable.Range(0, r.Atual.Dias).Select(i => dic != null && dic.TryGetValue(r.Atual.Inicio.AddDays(i), out var v) ? v : (decimal?)null).ToList()
        };
    }

    private static GraficoMarketing SeriePorPlataforma(Recorte r, string id, string titulo, string metrica, PlataformaMarketing[] plataformas, string tipo) => new()
    {
        Id = id, Titulo = titulo, Tipo = tipo, Rotulos = Rotulos(r.Atual),
        Series = plataformas.Where(p => r.Series.ContainsKey((p, metrica)))
            .Select(p => Serie(r, p, metrica, PlataformaInfo.Nome(p), PlataformaInfo.Cor(p))).ToList(),
        Nota = plataformas.Length > 1 ? "Uma linha por plataforma (valores não somados entre redes)." : null
    };

    /// <summary>Série de snapshots (ex.: seguidores), repetindo o último valor conhecido nos dias sem coleta.</summary>
    private static GraficoMarketing SerieSnapshot(Recorte r, string id, string titulo, string metrica, PlataformaMarketing[] plataformas)
    {
        var series = new List<SerieGrafico>();
        foreach (var p in plataformas)
        {
            var contas = r.DaPlataforma(p).Where(c => r.Snapshots.ContainsKey((c.Id, metrica))).ToList();
            if (contas.Count == 0) continue;
            series.Add(new SerieGrafico
            {
                Rotulo = PlataformaInfo.Nome(p), Cor = PlataformaInfo.Cor(p),
                Valores = Enumerable.Range(0, r.Atual.Dias).Select(i =>
                {
                    var dia = r.Atual.Inicio.AddDays(i);
                    var valores = contas.Select(c => r.SnapshotEm(c.Id, metrica, dia)).Where(v => v != null).ToList();
                    return valores.Count == 0 ? (decimal?)null : valores.Sum();
                }).ToList()
            });
        }
        return new GraficoMarketing
        {
            Id = id, Titulo = titulo, Tipo = "line", Rotulos = Rotulos(r.Atual), Series = series,
            Nota = "Montado a partir das sincronizações: o histórico começa na data em que a conta foi conectada."
        };
    }

    public async Task<List<(string Valor, decimal Total, Dictionary<string, decimal> Metricas)>> RankingDimensaoAsync(
        IEnumerable<ContaResumo> contas, PeriodoMarketing p, string dimensao, string metricaOrdem, int top)
    {
        var ids = contas.Select(c => c.Id).ToList();
        if (ids.Count == 0) return [];
        var linhas = await db.MetricasDimensao
            .Where(m => ids.Contains(m.ContaMarketingId) && m.Dimensao == dimensao && m.Data >= p.Inicio && m.Data <= p.Fim)
            .GroupBy(m => new { m.ValorDimensao, m.Metrica })
            .Select(g => new { g.Key.ValorDimensao, g.Key.Metrica, Total = g.Sum(x => x.Valor) })
            .ToListAsync();
        return linhas.GroupBy(l => l.ValorDimensao)
            .Select(g => (Valor: g.Key, Metricas: g.ToDictionary(x => x.Metrica, x => x.Total)))
            .Select(x => (x.Valor, Total: x.Metricas.GetValueOrDefault(metricaOrdem), x.Metricas))
            .OrderByDescending(x => x.Total).Take(top).ToList();
    }

    private static string N(decimal? v) => v is decimal d ? d.ToString("N0", PtBr) : "—";
    private static string Pct(decimal? v) => v is decimal d ? d.ToString("0.##", PtBr) + "%" : "—";

    private async Task<List<TabelaMarketing>> TabelasWebsiteAsync(List<ContaResumo> contas, PeriodoMarketing p)
    {
        var ga = contas.Where(c => c.Plataforma == PlataformaMarketing.GoogleAnalytics).ToList();
        var tabelas = new List<TabelaMarketing>();
        async Task Adicionar(string titulo, string dim, string ordem, string col1, (string Metrica, string Nome)[] colunas, int top = 10)
        {
            var rank = await RankingDimensaoAsync(ga, p, dim, ordem, top);
            tabelas.Add(new TabelaMarketing
            {
                Titulo = titulo,
                Colunas = [col1, .. colunas.Select(c => c.Nome)],
                Linhas = rank.Select(x => (string[])[x.Valor, .. colunas.Select(c => N(x.Metricas.GetValueOrDefault(c.Metrica)))]).ToList()
            });
        }
        await Adicionar("Origem / mídia", Dimensoes.OrigemMidia, Metricas.Sessoes, "Origem / mídia", [(Metricas.Sessoes, "Sessões")]);
        await Adicionar("Canais de aquisição", Dimensoes.Canal, Metricas.Sessoes, "Canal", [(Metricas.Sessoes, "Sessões"), (Metricas.UsuariosAtivos, "Usuários (soma diária)")]);
        await Adicionar("Páginas mais acessadas", Dimensoes.Pagina, Metricas.VisualizacoesPagina, "Página", [(Metricas.VisualizacoesPagina, "Visualizações"), (Metricas.UsuariosAtivos, "Usuários (soma diária)")]);
        await Adicionar("Campanhas", Dimensoes.Campanha, Metricas.Sessoes, "Campanha", [(Metricas.Sessoes, "Sessões"), (Metricas.EventosPrincipais, "Conversões")]);
        await Adicionar("Dispositivos", Dimensoes.Dispositivo, Metricas.Sessoes, "Dispositivo", [(Metricas.Sessoes, "Sessões"), (Metricas.UsuariosAtivos, "Usuários (soma diária)")]);
        await Adicionar("Localização (país)", Dimensoes.Pais, Metricas.Sessoes, "País", [(Metricas.Sessoes, "Sessões"), (Metricas.UsuariosAtivos, "Usuários (soma diária)")]);
        await Adicionar("Eventos", Dimensoes.Evento, Metricas.Eventos, "Evento", [(Metricas.Eventos, "Ocorrências"), (Metricas.EventosPrincipais, "Como evento principal")]);
        return tabelas;
    }

    private async Task<List<TabelaMarketing>> TabelasSeoAsync(List<ContaResumo> contas, PeriodoMarketing p)
    {
        var gsc = contas.Where(c => c.Plataforma == PlataformaMarketing.SearchConsole).ToList();
        var tabelas = new List<TabelaMarketing>();
        async Task Adicionar(string titulo, string dim, string col1, int top)
        {
            var rank = await RankingDimensaoAsync(gsc, p, dim, Metricas.CliquesOrganicos, top);
            tabelas.Add(new TabelaMarketing
            {
                Titulo = titulo,
                Colunas = [col1, "Cliques", "Impressões", "CTR", "Posição média"],
                Linhas = rank.Select(x =>
                {
                    var cl = x.Metricas.GetValueOrDefault(Metricas.CliquesOrganicos);
                    var im = x.Metricas.GetValueOrDefault(Metricas.ImpressoesOrganicas);
                    var pos = CalculosMarketing.Divisao(x.Metricas.GetValueOrDefault(Metricas.PosicaoPonderada), im);
                    return new[] { x.Valor, N(cl), N(im), Pct(CalculosMarketing.Ctr(cl, im)), pos is decimal d ? d.ToString("0.0", PtBr) : "—" };
                }).ToList()
            });
        }
        await Adicionar("Principais palavras-chave", Dimensoes.PalavraChave, "Consulta", 15);
        await Adicionar("Páginas na pesquisa orgânica", Dimensoes.Pagina, "Página", 15);
        await Adicionar("Desempenho por dispositivo", Dimensoes.Dispositivo, "Dispositivo", 5);
        await Adicionar("Desempenho por país", Dimensoes.Pais, "País", 10);
        return tabelas;
    }

    public async Task<List<PublicacaoMarketing>> MelhoresPublicacoesAsync(List<int> contaIds, PeriodoMarketing p, int top)
    {
        if (contaIds.Count == 0) return [];
        var ini = p.Inicio.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var fim = p.Fim.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return await db.PublicacoesMarketing
            .Where(x => contaIds.Contains(x.ContaMarketingId) && x.PublicadoEm >= ini && x.PublicadoEm < fim)
            .OrderByDescending(x => x.Interacoes ?? 0).ThenByDescending(x => x.Visualizacoes ?? 0)
            .Take(top).ToListAsync();
    }

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);
}
