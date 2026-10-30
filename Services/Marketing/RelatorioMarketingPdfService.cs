using System.Globalization;
using System.Text;
using AgenciaOS.Models;
using AgenciaOS.ViewModels;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AgenciaOS.Services.Marketing;

/// <summary>Gera o PDF do relatório de marketing a partir dos dados já sincronizados.</summary>
public class RelatorioMarketingPdfService(PainelMarketingService painel, IGeradorInsights insights)
{
    private static readonly CultureInfo PtBr = new("pt-BR");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static RelatorioMarketingPdfService()
    {
        // Licença Community: gratuita para empresas com receita bruta anual abaixo de US$ 1 milhão.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static readonly (string Aba, string Titulo, PlataformaMarketing? Plataforma)[] Secoes =
    [
        ("instagram", "Instagram", PlataformaMarketing.Instagram),
        ("facebook", "Facebook", PlataformaMarketing.Facebook),
        ("tiktok", "TikTok", PlataformaMarketing.TikTok),
        ("website", "Website (Google Analytics 4)", PlataformaMarketing.GoogleAnalytics),
        ("seo", "SEO (Google Search Console)", PlataformaMarketing.SearchConsole),
    ];

    public async Task<byte[]> GerarAsync(AcessoMarketingService.Contexto ctx, RelatorioMarketing rel, string nomeCliente, string corPrimaria, string nomeAgencia)
    {
        var plataformas = rel.Plataformas.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var indicadores = rel.Indicadores?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        FiltroMarketing Filtro(string aba) => new()
        {
            ClienteId = rel.ClienteId, Periodo = "personalizado", De = rel.PeriodoInicio, Ate = rel.PeriodoFim, Aba = aba
        };

        var geral = await painel.MontarAsync(ctx, Filtro("geral"));
        var secoes = new List<(string Titulo, PainelMarketingViewModel Vm)>();
        foreach (var s in Secoes.Where(s => plataformas.Contains(s.Plataforma.ToString()!)))
        {
            var vm = await painel.MontarAsync(ctx, Filtro(s.Aba));
            if (vm.Contas.Count > 0) secoes.Add((s.Titulo, vm));
        }
        var listaInsights = await insights.GerarAsync(ctx, Filtro("geral"));

        bool Mostrar(KpiMarketing k) => (indicadores == null || indicadores.Count == 0 || indicadores.Contains(k.Chave))
                                        && (k.Valor != null || k.Fontes.Count > 0);

        var cor = Color.FromHex(corPrimaria.Length == 7 ? corPrimaria : "#6366f1");

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor("#1e293b"));

                page.Header().Column(h =>
                {
                    h.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text(rel.Titulo).FontSize(18).Bold().FontColor(cor);
                            c.Item().Text($"{nomeCliente} · {rel.PeriodoInicio:dd/MM/yyyy} a {rel.PeriodoFim:dd/MM/yyyy}").FontSize(10).FontColor("#64748b");
                        });
                        row.ConstantItem(160).AlignRight().Text($"{nomeAgencia}\nGerado em {DateTime.UtcNow.AddHours(-3):dd/MM/yyyy HH:mm}").FontSize(8).FontColor("#94a3b8");
                    });
                    h.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(cor);
                });

                page.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(12);

                    // Resumo
                    col.Item().Text("Resumo dos resultados").FontSize(13).Bold();
                    var destaques = geral.Kpis.Where(k => k.Valor != null && Mostrar(k)).ToList();
                    col.Item().Text(destaques.Count == 0
                        ? "Não há dados sincronizados para o período selecionado."
                        : string.Join(" ", destaques.Take(6).Select(k =>
                            $"{k.Titulo}: {Formatar(k.Valor, k.Formato)}" + (rel.CompararPeriodoAnterior && k.Variacao is decimal v ? $" ({Sinal(v)} vs período anterior)." : "."))));

                    col.Item().Element(c => GradeKpis(c, geral.Kpis.Where(Mostrar), rel.CompararPeriodoAnterior));
                    foreach (var g in geral.Graficos.Where(g => !g.Vazio && g.Tipo != "doughnut").Take(3))
                        col.Item().Element(c => Grafico(c, g));

                    foreach (var (titulo, vm) in secoes)
                    {
                        col.Item().PageBreak();
                        col.Item().Text(titulo).FontSize(14).Bold().FontColor(cor);
                        col.Item().Text(string.Join(" · ", vm.Contas.Select(c => c.NomeUsuario != null ? "@" + c.NomeUsuario : c.Nome))).FontColor("#64748b");
                        col.Item().Element(c => GradeKpis(c, vm.Kpis.Where(Mostrar), rel.CompararPeriodoAnterior));
                        foreach (var g in vm.Graficos.Where(g => !g.Vazio && g.Tipo != "doughnut").Take(2))
                            col.Item().Element(c => Grafico(c, g));
                        foreach (var t in vm.Tabelas.Where(t => t.Linhas.Count > 0).Take(4))
                            col.Item().Element(c => Tabela(c, t));
                    }

                    if (geral.MelhoresPublicacoes.Count > 0)
                    {
                        col.Item().PageBreak();
                        col.Item().Text("Melhores publicações").FontSize(14).Bold().FontColor(cor);
                        col.Item().Element(c => Tabela(c, new TabelaMarketing
                        {
                            Colunas = ["Data", "Plataforma", "Publicação", "Visualizações", "Alcance", "Interações"],
                            Linhas = geral.MelhoresPublicacoes.Select(p => new[]
                            {
                                p.PublicadoEm.ToString("dd/MM/yyyy"), p.Plataforma.ToString(),
                                Cortar(p.Legenda, 70), N(p.Visualizacoes), N(p.Alcance), N(p.Interacoes)
                            }).ToList()
                        }));
                    }

                    col.Item().Text("Insights").FontSize(14).Bold().FontColor(cor);
                    foreach (var i in listaInsights)
                        col.Item().Column(c =>
                        {
                            c.Item().Text(i.Titulo).Bold();
                            c.Item().Text(i.Texto).FontColor("#475569");
                        });

                    col.Item().PaddingTop(8).Text("Metodologia: métricas de plataformas diferentes não são somadas quando têm definições distintas (ex.: alcance). " +
                        "Taxas: engajamento por seguidores = interações / seguidores × 100; CTR = cliques / impressões × 100; variação = (atual − anterior) / anterior × 100. " +
                        "Dados do Search Console e do Google Analytics são fontes diferentes e não são combinados.").FontSize(7.5f).FontColor("#94a3b8");
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.Span("Página ").FontSize(8).FontColor("#94a3b8");
                    t.CurrentPageNumber().FontSize(8).FontColor("#94a3b8");
                    t.Span(" de ").FontSize(8).FontColor("#94a3b8");
                    t.TotalPages().FontSize(8).FontColor("#94a3b8");
                });
            });
        }).GeneratePdf();
    }

    private static void GradeKpis(IContainer container, IEnumerable<KpiMarketing> kpis, bool comparar)
    {
        var lista = kpis.ToList();
        if (lista.Count == 0) return;
        container.Table(t =>
        {
            t.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
            foreach (var k in lista)
            {
                t.Cell().Padding(3).Border(0.6f).BorderColor("#e2e8f0").Background("#f8fafc").Padding(7).Column(c =>
                {
                    c.Item().Text(k.Titulo).FontSize(8).FontColor("#64748b");
                    if (k.Valor != null)
                        c.Item().Text(Formatar(k.Valor, k.Formato)).FontSize(14).Bold();
                    else
                        foreach (var f in k.Fontes.Take(3))
                            c.Item().Text($"{f.Conta}: {Formatar(f.Valor, k.Formato)}").FontSize(8.5f);
                    if (comparar && k.Variacao is decimal v)
                        c.Item().Text($"{Sinal(v)} vs período anterior").FontSize(8).FontColor(v >= 0 ? "#059669" : "#dc2626");
                    c.Item().Text($"Fonte: {k.FontesTexto}").FontSize(7).FontColor("#94a3b8");
                });
            }
        });
    }

    private static void Grafico(IContainer container, GraficoMarketing g)
    {
        container.Column(c =>
        {
            c.Item().Text(g.Titulo).Bold().FontSize(10);
            c.Item().Height(150).Svg(SvgLinhas(g, 520, 150));
            c.Item().Text(string.Join("   ", g.Series.Select(s => $"■ {s.Rotulo}"))).FontSize(7.5f).FontColor("#64748b");
        });
    }

    /// <summary>Gráfico de linhas simples em SVG (cada série com escala própria do eixo Y comum).</summary>
    internal static string SvgLinhas(GraficoMarketing g, int w, int h)
    {
        const int esq = 40, base_ = 18, topo = 6;
        var max = g.Series.SelectMany(s => s.Valores).Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        if (max <= 0) max = 1;
        var n = Math.Max(1, g.Rotulos.Count - 1);
        var sb = new StringBuilder(string.Create(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\">"));
        for (var i = 0; i <= 4; i++)
        {
            var y = topo + (h - base_ - topo) * i / 4.0;
            sb.Append(Inv, $"<line x1=\"{esq}\" y1=\"{y:0.#}\" x2=\"{w}\" y2=\"{y:0.#}\" stroke=\"#e2e8f0\" stroke-width=\"0.6\"/>");
            sb.Append(Inv, $"<text x=\"{esq - 4}\" y=\"{y + 3:0.#}\" font-size=\"7\" fill=\"#94a3b8\" text-anchor=\"end\">{(max * (4 - i) / 4).ToString("N0", PtBr)}</text>");
        }
        var passo = Math.Max(1, g.Rotulos.Count / 8);
        for (var i = 0; i < g.Rotulos.Count; i += passo)
            sb.Append(Inv, $"<text x=\"{esq + (w - esq) * i / (double)n:0.#}\" y=\"{h - 4}\" font-size=\"7\" fill=\"#94a3b8\" text-anchor=\"middle\">{g.Rotulos[i]}</text>");
        foreach (var s in g.Series)
        {
            var pontos = s.Valores.Select((v, i) => v is decimal d
                ? string.Create(Inv, $"{esq + (w - esq) * i / (double)n:0.#},{topo + (h - base_ - topo) * (1 - (double)(d / max)):0.#}")
                : null).Where(p => p != null);
            sb.Append(Inv, $"<polyline fill=\"none\" stroke=\"{s.Cor}\" stroke-width=\"1.6\" points=\"{string.Join(" ", pontos)}\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Tabela(IContainer container, TabelaMarketing t)
    {
        container.Column(c =>
        {
            if (!string.IsNullOrEmpty(t.Titulo)) c.Item().PaddingBottom(3).Text(t.Titulo).Bold().FontSize(10);
            c.Item().Table(tab =>
            {
                tab.ColumnsDefinition(cd =>
                {
                    cd.RelativeColumn(3);
                    for (var i = 1; i < t.Colunas.Length; i++) cd.RelativeColumn();
                });
                foreach (var h in t.Colunas)
                    tab.Cell().Background("#f1f5f9").Padding(4).Text(h).FontSize(8).Bold();
                foreach (var linha in t.Linhas)
                foreach (var v in linha)
                    tab.Cell().BorderBottom(0.5f).BorderColor("#e2e8f0").Padding(4).Text(Cortar(v, 80)).FontSize(8);
            });
        });
    }

    internal static string Formatar(decimal? v, FormatoKpi f) => v is not decimal d ? "—" : f switch
    {
        FormatoKpi.Percentual => d.ToString("0.##", PtBr) + "%",
        FormatoKpi.Segundos => TimeSpan.FromSeconds((double)d).ToString(@"m\:ss") + " min",
        FormatoKpi.Posicao => d.ToString("0.0", PtBr),
        _ => d.ToString("N0", PtBr)
    };

    private static string Sinal(decimal v) => (v >= 0 ? "+" : "") + v.ToString("0.#", PtBr) + "%";
    private static string N(long? v) => v is long l ? l.ToString("N0", PtBr) : "—";
    private static string Cortar(string? s, int n) => string.IsNullOrEmpty(s) ? "—" : (s.Length > n ? s[..n] + "…" : s).ReplaceLineEndings(" ");
}
