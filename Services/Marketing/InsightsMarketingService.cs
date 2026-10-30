using System.Globalization;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing.Provedores;
using AgenciaOS.ViewModels;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Gera análises a partir dos dados sincronizados. A interface permite trocar/combinar com um gerador
/// baseado em IA no futuro (ex.: enviando os mesmos indicadores como contexto), sem mudar as telas.
/// </summary>
public interface IGeradorInsights
{
    Task<List<InsightMarketing>> GerarAsync(AcessoMarketingService.Contexto ctx, FiltroMarketing filtro);
}

public class InsightsMarketingService(PainelMarketingService painel) : IGeradorInsights
{
    private static readonly CultureInfo PtBr = new("pt-BR");
    private const decimal LimiarVariacao = 5m;

    public async Task<List<InsightMarketing>> GerarAsync(AcessoMarketingService.Contexto ctx, FiltroMarketing filtro)
    {
        var periodo = PeriodoMarketing.Criar(filtro.Periodo, filtro.De, filtro.Ate, DateOnly.FromDateTime(DateTime.UtcNow));
        var contas = await painel.ContasAsync(ctx, new FiltroMarketing { ClienteId = filtro.ClienteId, Plataforma = filtro.Plataforma, ContaId = filtro.ContaId });
        var r = await painel.CarregarAsync(contas, periodo);
        var lista = new List<InsightMarketing>();

        if (contas.Count == 0)
        {
            lista.Add(new InsightMarketing
            {
                Titulo = "Sem contas conectadas", Icone = "bi-plug",
                Texto = "Conecte as redes sociais e o site em Contas e Integrações para gerar análises com dados reais."
            });
            return lista;
        }

        Crescimento(r, lista);
        Engajamento(r, lista);
        await MelhoresConteudos(r, lista);
        MelhorDiaAlcance(r, lista);
        Trafego(r, lista);
        await Canais(r, lista);
        Seo(r, lista);
        Variacoes(r, lista);

        if (lista.Count == 0)
            lista.Add(new InsightMarketing
            {
                Titulo = "Dados insuficientes", Icone = "bi-hourglass-split",
                Texto = "Ainda não há dados sincronizados suficientes neste período para gerar análises."
            });
        return lista;
    }

    private static string N(decimal v) => v.ToString("N0", PtBr);
    private static string P(decimal v) => v.ToString("0.#", PtBr) + "%";

    private void Crescimento(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var porPlataforma = PlataformaInfo.Sociais.Select(p =>
        {
            // só contas com histórico nas duas pontas, para não comparar bases diferentes
            var pares = r.DaPlataforma(p)
                .Select(c => (Fim: r.SnapshotEm(c.Id, Metricas.Seguidores, r.Atual.Fim), Ini: r.SnapshotEm(c.Id, Metricas.Seguidores, r.Atual.Inicio.AddDays(-1))))
                .Where(x => x.Fim != null && x.Ini != null).ToList();
            var fim = Soma(pares.Select(x => x.Fim));
            var ini = Soma(pares.Select(x => x.Ini));
            return (p, Taxa: CalculosMarketing.CrescimentoSeguidores(fim, ini), Liquido: fim - ini);
        }).Where(x => x.Taxa != null).OrderByDescending(x => x.Taxa).ToList();

        if (porPlataforma.Count == 0) return;
        var melhor = porPlataforma[0];
        lista.Add(new InsightMarketing
        {
            Titulo = "Rede com maior crescimento", Icone = "bi-graph-up-arrow", Tom = melhor.Taxa >= 0 ? "positivo" : "negativo",
            Fonte = PlataformaInfo.Nome(melhor.p),
            Texto = $"{PlataformaInfo.Nome(melhor.p)} teve a maior taxa de crescimento de seguidores: {P(melhor.Taxa!.Value)} " +
                    $"({(melhor.Liquido >= 0 ? "+" : "")}{N(melhor.Liquido ?? 0)} seguidores) em {r.Atual.Rotulo.ToLowerInvariant()}." +
                    (porPlataforma.Count > 1 ? $" Comparação: {string.Join(", ", porPlataforma.Skip(1).Select(x => $"{PlataformaInfo.Nome(x.p)} {P(x.Taxa!.Value)}"))}." : "")
        });
    }

    private void Engajamento(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var taxas = PlataformaInfo.Sociais
            .Select(p => (p, Kpi: painel.KpiEngajamento(r, [p])))
            .Where(x => x.Kpi.Valor != null).OrderByDescending(x => x.Kpi.Valor).ToList();
        if (taxas.Count == 0) return;
        var melhor = taxas[0];
        lista.Add(new InsightMarketing
        {
            Titulo = "Plataforma com mais engajamento", Icone = "bi-heart-pulse-fill", Tom = "positivo", Fonte = PlataformaInfo.Nome(melhor.p),
            Texto = $"{PlataformaInfo.Nome(melhor.p)} gerou o maior engajamento por seguidores: {P(melhor.Kpi.Valor!.Value)} (interações / seguidores × 100)." +
                    (taxas.Count > 1 ? $" Demais: {string.Join(", ", taxas.Skip(1).Select(x => $"{PlataformaInfo.Nome(x.p)} {P(x.Kpi.Valor!.Value)}"))}." : "")
        });
    }

    private async Task MelhoresConteudos(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var top = await painel.MelhoresPublicacoesAsync(r.Contas.Select(c => c.Id).ToList(), r.Atual, 3);
        if (top.Count == 0) return;
        lista.Add(new InsightMarketing
        {
            Titulo = "Publicações com melhor desempenho", Icone = "bi-star-fill", Tom = "positivo",
            Texto = string.Join(" ", top.Select((p, i) =>
                $"{i + 1}º {PlataformaInfo.Nome(p.Plataforma)} em {p.PublicadoEm:dd/MM}: {N(p.Interacoes ?? 0)} interações" +
                (p.Visualizacoes is long v ? $", {N(v)} visualizações" : "") +
                $" — \"{Resumo(p.Legenda)}\"."))
        });
    }

    private void MelhorDiaAlcance(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        foreach (var p in PlataformaInfo.Sociais)
        {
            if (!r.Series.TryGetValue((p, Metricas.Alcance), out var serie) || serie.Count == 0) continue;
            var melhor = serie.MaxBy(x => x.Value);
            if (melhor.Value <= 0) continue;
            var porDiaSemana = serie.GroupBy(x => x.Key.DayOfWeek).Select(g => (Dia: g.Key, Media: g.Average(x => x.Value))).MaxBy(x => x.Media);
            lista.Add(new InsightMarketing
            {
                Titulo = $"Dia de maior alcance no {PlataformaInfo.Nome(p)}", Icone = "bi-calendar-check-fill", Fonte = PlataformaInfo.Nome(p),
                Texto = $"O maior alcance diário foi em {melhor.Key.ToString("dddd, dd/MM", PtBr)}: {N(melhor.Value)} contas alcançadas. " +
                        $"Na média do período, {PtBr.DateTimeFormat.GetDayName(porDiaSemana.Dia)} foi o dia da semana com mais alcance."
            });
        }
    }

    private static void Trafego(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var ga = r.DaPlataforma(PlataformaMarketing.GoogleAnalytics).ToList();
        if (ga.Count == 0) return;
        var atual = Soma(ga.Select(c => r.Soma(c.Id, Metricas.Sessoes, true)));
        var anterior = Soma(ga.Select(c => r.Soma(c.Id, Metricas.Sessoes, false)));
        if (atual is null) return;
        var variacao = CalculosMarketing.Variacao(atual, anterior);
        lista.Add(new InsightMarketing
        {
            Titulo = "Evolução do tráfego do site", Icone = "bi-globe2", Fonte = "Google Analytics 4",
            Tom = variacao switch { > 0 => "positivo", < 0 => "negativo", _ => "neutro" },
            Texto = variacao is decimal v
                ? $"O site recebeu {N(atual.Value)} sessões, {(v >= 0 ? "alta" : "queda")} de {P(Math.Abs(v))} em relação ao período anterior ({N(anterior ?? 0)} sessões)."
                : $"O site recebeu {N(atual.Value)} sessões. Não há dados do período anterior para comparar."
        });
    }

    private async Task Canais(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var canais = await painel.RankingDimensaoAsync(r.DaPlataforma(PlataformaMarketing.GoogleAnalytics), r.Atual, Dimensoes.Canal, Metricas.Sessoes, 10);
        var total = canais.Sum(c => c.Total);
        if (total <= 0) return;
        lista.Add(new InsightMarketing
        {
            Titulo = "Canais que mais trazem visitantes", Icone = "bi-signpost-split-fill", Fonte = "Google Analytics 4",
            Texto = string.Join(", ", canais.Take(3).Select(c => $"{c.Valor} ({P(c.Total / total * 100)} das sessões)")) + "."
        });
    }

    private void Seo(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var gsc = PlataformaMarketing.SearchConsole;
        if (!r.DaPlataforma(gsc).Any()) return;
        var ctr = painel.KpiRazao(r, "ctr", "CTR", "", "", gsc, Metricas.CliquesOrganicos, Metricas.ImpressoesOrganicas, 100, FormatoKpi.Percentual, "");
        var pos = painel.KpiRazao(r, "posicao", "Posição", "", "", gsc, Metricas.PosicaoPonderada, Metricas.ImpressoesOrganicas, 1, FormatoKpi.Posicao, "");
        if (ctr.Valor is null && pos.Valor is null) return;
        var partes = new List<string>();
        if (ctr.Valor is decimal c) partes.Add($"CTR orgânico de {P(c)}" + (ctr.Anterior is decimal ca ? $" (antes {P(ca)})" : ""));
        if (pos.Valor is decimal p) partes.Add($"posição média {p.ToString("0.0", PtBr)}" + (pos.Anterior is decimal pa ? $" (antes {pa.ToString("0.0", PtBr)})" : ""));
        lista.Add(new InsightMarketing
        {
            Titulo = "Pesquisa orgânica (SEO)", Icone = "bi-search", Fonte = "Google Search Console",
            Tom = pos.Variacao switch { > 0 => "positivo", < 0 => "negativo", _ => "neutro" },
            Texto = string.Join("; ", partes) + "."
        });
    }

    private void Variacoes(PainelMarketingService.Recorte r, List<InsightMarketing> lista)
    {
        var sociais = PlataformaInfo.Sociais;
        var kpis = new[]
        {
            painel.KpiSeguidores(r, sociais),
            painel.KpiSoma(r, "interacoes", "Interações", "", "", Metricas.Interacoes, sociais, true),
            painel.KpiSoma(r, "sessoes", "Sessões no site", "", "", Metricas.Sessoes, [PlataformaMarketing.GoogleAnalytics], false),
            painel.KpiSoma(r, "conversoes", "Conversões", "", "", Metricas.EventosPrincipais, [PlataformaMarketing.GoogleAnalytics], false),
            painel.KpiSoma(r, "cliques_organicos", "Cliques orgânicos", "", "", Metricas.CliquesOrganicos, [PlataformaMarketing.SearchConsole], false),
        }.Concat(sociais.Select(p => painel.KpiSoma(r, "alcance", $"Alcance no {PlataformaInfo.Nome(p)}", "", "", Metricas.Alcance, [p], false)));

        var avaliados = kpis.Where(k => k.Variacao != null).ToList();
        var melhoraram = avaliados.Where(k => k.Variacao >= LimiarVariacao).OrderByDescending(k => k.Variacao).ToList();
        var pioraram = avaliados.Where(k => k.Variacao <= -LimiarVariacao).OrderBy(k => k.Variacao).ToList();

        if (melhoraram.Count > 0)
            lista.Add(new InsightMarketing
            {
                Titulo = "Indicadores que melhoraram", Icone = "bi-arrow-up-circle-fill", Tom = "positivo",
                Texto = string.Join(", ", melhoraram.Select(k => $"{k.Titulo} (+{P(k.Variacao!.Value)})")) + " em relação ao período anterior."
            });
        if (pioraram.Count > 0)
            lista.Add(new InsightMarketing
            {
                Titulo = "Indicadores que pioraram", Icone = "bi-arrow-down-circle-fill", Tom = "negativo",
                Texto = string.Join(", ", pioraram.Select(k => $"{k.Titulo} ({P(k.Variacao!.Value)})")) + " em relação ao período anterior."
            });
    }

    private static decimal? Soma(IEnumerable<decimal?> valores)
    {
        var lista = valores.Where(v => v != null).ToList();
        return lista.Count == 0 ? null : lista.Sum();
    }

    private static string Resumo(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "sem legenda";
        texto = texto.ReplaceLineEndings(" ");
        return texto.Length > 60 ? texto[..60] + "…" : texto;
    }
}
