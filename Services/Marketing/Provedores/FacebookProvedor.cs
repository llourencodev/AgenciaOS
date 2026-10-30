using System.Text.Json;
using AgenciaOS.Models;
using static AgenciaOS.Services.Marketing.Provedores.JsonExt;

namespace AgenciaOS.Services.Marketing.Provedores;

/// <summary>
/// Páginas do Facebook via Meta Graph API (Facebook Login).
/// Escopos mínimos: pages_show_list, pages_read_engagement, read_insights.
/// </summary>
public class FacebookProvedor(ClienteHttpMarketing http, MarketingOptions opt) : IProvedorMarketing
{
    public const string Escopos = "pages_show_list,pages_read_engagement,read_insights";

    public PlataformaMarketing Plataforma => PlataformaMarketing.Facebook;
    public string Fonte => "Meta Graph API (Facebook Pages)";
    private string Graph => $"https://graph.facebook.com/{opt.MetaGraphVersion}";

    // Métrica normalizada → nomes na API em ordem de preferência (a Meta renomeia/deprecia métricas com frequência)
    internal static readonly (string Chave, string[] Api)[] MetricasPagina =
    [
        (Metricas.Alcance,            ["page_impressions_unique"]),
        (Metricas.Impressoes,         ["page_impressions"]),
        (Metricas.Visualizacoes,      ["page_media_view"]),
        (Metricas.VisitasPerfil,      ["page_views_total"]),
        (Metricas.Interacoes,         ["page_post_engagements"]),
        (Metricas.NovosSeguidores,    ["page_daily_follows_unique", "page_fan_adds_unique"]),
        (Metricas.VisualizacoesVideo, ["page_video_views"]),
        (Metricas.Cliques,            ["page_total_actions"]),
        (Metricas.Curtidas,           ["page_actions_post_reactions_total"]),
    ];

    public string MontarUrlAutorizacao(string state, string redirectUri, string codeChallenge) =>
        $"https://www.facebook.com/{opt.MetaGraphVersion}/dialog/oauth?client_id={Q(opt.MetaAppId!)}" +
        $"&redirect_uri={Q(redirectUri)}&state={Q(state)}&response_type=code&scope={Q(Escopos)}";

    public async Task<ResultadoToken> TrocarCodigoAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var curto = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm($"{Graph}/oauth/access_token",
        [
            new("client_id", opt.MetaAppId!), new("client_secret", opt.MetaAppSecret!),
            new("redirect_uri", redirectUri), new("code", code)
        ]), MetaErros.Mapear, "facebook.token", ct);

        // Troca por token de longa duração (~60 dias). Tokens de página derivados dele não expiram.
        var longo = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm($"{Graph}/oauth/access_token",
        [
            new("grant_type", "fb_exchange_token"), new("client_id", opt.MetaAppId!),
            new("client_secret", opt.MetaAppSecret!), new("fb_exchange_token", curto.Str("access_token")!)
        ]), MetaErros.Mapear, "facebook.token_longo", ct);

        var token = longo.Str("access_token")!;
        var expira = longo.Long("expires_in") is long s ? DateTime.UtcNow.AddSeconds(s) : (DateTime?)null;
        var me = await http.EnviarAsync(() => ClienteHttpMarketing.Get($"{Graph}/me?fields=id,name&access_token={Q(token)}"),
            MetaErros.Mapear, "facebook.me", ct);
        var permissoes = await http.EnviarAsync(() => ClienteHttpMarketing.Get($"{Graph}/me/permissions?access_token={Q(token)}"),
            MetaErros.Mapear, "facebook.permissions", ct);
        var concedidas = permissoes.Itens("data").Where(p => p.Str("status") == "granted").Select(p => p.Str("permission"));

        return new ResultadoToken(token, null, expira, null, string.Join(",", concedidas), me.Str("id"), me.Str("name"));
    }

    public Task<ResultadoToken?> RenovarAsync(ConexaoMarketing conexao, string accessToken, string? refreshToken, CancellationToken ct) =>
        Task.FromResult<ResultadoToken?>(null);

    public async Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct)
    {
        var contas = new List<ContaDisponivel>();
        string? url = $"{Graph}/me/accounts?fields=id,name,access_token,picture{{url}},followers_count,fan_count,category&limit=100&access_token={Q(accessToken)}";
        for (var pagina = 0; url != null && pagina < 10; pagina++)
        {
            var atual = url;
            var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get(atual), MetaErros.Mapear, "facebook.accounts", ct);
            foreach (var p in json.Itens("data"))
            {
                contas.Add(new ContaDisponivel(p.Str("id")!, p.Str("name") ?? "Página",
                    null, p.Prop("picture").Prop("data").Str("url"), p.Str("category"))
                { TokenRecurso = p.Str("access_token") });
            }
            url = json.Prop("paging").Str("next");
        }
        return contas;
    }

    public async Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct)
    {
        var dados = new DadosSincronizados();
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var perfil = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"{Graph}/{conta.IdExterno}?fields=name,picture{{url}},followers_count,fan_count&access_token={Q(token)}"),
            MetaErros.Mapear, "facebook.page", ct);
        dados.Nome = perfil.Str("name");
        dados.FotoUrl = perfil.Prop("picture").Prop("data").Str("url");
        dados.Adicionar(hoje, Metricas.Seguidores, perfil.Dec("followers_count") ?? perfil.Dec("fan_count"));

        // Insights diários da página, em blocos de até 90 dias (limite da API: 93)
        foreach (var (chave, nomesApi) in MetricasPagina)
        {
            var obtida = false;
            foreach (var nomeApi in nomesApi)
            {
                try
                {
                    for (var ini = desde; ini <= ate; ini = ini.AddDays(90))
                    {
                        var fim = ini.AddDays(89) < ate ? ini.AddDays(89) : ate;
                        var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
                            $"{Graph}/{conta.IdExterno}/insights?metric={nomeApi}&period=day" +
                            $"&since={Unix(ini)}&until={Unix(fim.AddDays(1))}&access_token={Q(token)}"),
                            MetaErros.Mapear, "facebook.insights", ct);
                        foreach (var (data, valor) in LerSerieInsights(json))
                            dados.Adicionar(data, chave, valor);
                    }
                    obtida = true;
                    break;
                }
                catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado) { }
            }
            if (!obtida) dados.Indisponiveis.Add(chave);
        }

        await SincronizarPostsAsync(conta, token, dados, ct);
        return dados;
    }

    private async Task SincronizarPostsAsync(ContaMarketing conta, string token, DadosSincronizados dados, CancellationToken ct)
    {
        var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"{Graph}/{conta.IdExterno}/posts?fields=id,message,created_time,permalink_url,full_picture,status_type,shares," +
            $"reactions.summary(total_count).limit(0),comments.summary(total_count).limit(0)&limit=25&access_token={Q(token)}"),
            MetaErros.Mapear, "facebook.posts", ct);

        // Descobre uma vez quais métricas de post esta página suporta
        string[] candidatas = ["post_impressions_unique", "post_clicks", "post_video_views"];
        List<string>? suportadas = null;

        foreach (var p in json.Itens("data"))
        {
            var pub = new PublicacaoColetada
            {
                IdExterno = p.Str("id")!,
                Tipo = p.Str("status_type"),
                Legenda = p.Str("message"),
                Link = p.Str("permalink_url"),
                ThumbnailUrl = p.Str("full_picture"),
                PublicadoEm = DateTimeOffset.TryParse(p.Str("created_time"), out var dt) ? dt.UtcDateTime : DateTime.UtcNow,
                Curtidas = p.Prop("reactions").Prop("summary").Long("total_count"),
                Comentarios = p.Prop("comments").Prop("summary").Long("total_count"),
                Compartilhamentos = p.Prop("shares").Long("count") ?? 0,
            };

            suportadas ??= await DescobrirMetricasPostAsync(pub.IdExterno, token, candidatas, ct);
            if (suportadas.Count > 0)
            {
                try
                {
                    var ins = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
                        $"{Graph}/{pub.IdExterno}/insights?metric={string.Join(",", suportadas)}&access_token={Q(token)}"),
                        MetaErros.Mapear, "facebook.post_insights", ct);
                    foreach (var m in ins.Itens("data"))
                    {
                        var valor = m.Itens("values").Select(v => (long?)ParseDec(v.Prop("value"))).FirstOrDefault();
                        switch (m.Str("name"))
                        {
                            case "post_impressions_unique": pub.Alcance = valor; break;
                            case "post_clicks": pub.Cliques = valor; break;
                            case "post_video_views": pub.Visualizacoes = valor; break;
                        }
                    }
                }
                catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado) { }
            }

            pub.Interacoes = (pub.Curtidas ?? 0) + (pub.Comentarios ?? 0) + (pub.Compartilhamentos ?? 0);
            dados.Publicacoes.Add(pub);
        }
    }

    private async Task<List<string>> DescobrirMetricasPostAsync(string postId, string token, string[] candidatas, CancellationToken ct)
    {
        var ok = new List<string>();
        foreach (var m in candidatas)
        {
            try
            {
                await http.EnviarAsync(() => ClienteHttpMarketing.Get($"{Graph}/{postId}/insights?metric={m}&access_token={Q(token)}"),
                    MetaErros.Mapear, "facebook.post_insights_probe", ct);
                ok.Add(m);
            }
            catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado) { }
        }
        return ok;
    }

    public async Task<bool> RevogarAsync(string accessToken, CancellationToken ct)
    {
        await http.EnviarAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{Graph}/me/permissions?access_token={Q(accessToken)}"),
            MetaErros.Mapear, "facebook.revoke", ct);
        return true;
    }

    internal static long Unix(DateOnly d) => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>
    /// Lê séries de insights da Graph API. O end_time marca o fim do dia (horário do Pacífico),
    /// então o valor pertence ao dia anterior a ele.
    /// </summary>
    internal static IEnumerable<(DateOnly Data, decimal Valor)> LerSerieInsights(JsonElement json)
    {
        foreach (var metrica in json.Itens("data"))
        foreach (var v in metrica.Itens("values"))
        {
            if (!DateTimeOffset.TryParse(v.Str("end_time"), out var fim)) continue;
            var data = DateOnly.FromDateTime(fim.UtcDateTime.AddHours(-8).AddSeconds(-1));
            var valor = v.Prop("value");
            decimal? numero = valor.ValueKind == JsonValueKind.Object
                ? valor.EnumerateObject().Sum(x => ParseDec(x.Value) ?? 0) // ex.: reações por tipo
                : ParseDec(valor);
            if (numero is decimal n) yield return (data, n);
        }
    }
}
