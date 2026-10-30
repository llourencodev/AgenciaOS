using System.Globalization;
using System.Net;
using System.Text.Json;
using AgenciaOS.Models;
using static AgenciaOS.Services.Marketing.Provedores.JsonExt;

namespace AgenciaOS.Services.Marketing.Provedores;

/// <summary>Dimensões normalizadas guardadas em MetricaDimensao.</summary>
public static class Dimensoes
{
    public const string Canal       = "canal";
    public const string OrigemMidia = "origem_midia";
    public const string Campanha    = "campanha";
    public const string Pagina      = "pagina";
    public const string Dispositivo = "dispositivo";
    public const string Pais        = "pais";
    public const string Evento      = "evento";
    public const string PalavraChave = "palavra_chave";
}

/// <summary>OAuth 2.0 do Google (offline + PKCE S256), compartilhado por GA4 e Search Console.</summary>
public abstract class GoogleProvedorBase(ClienteHttpMarketing http, MarketingOptions opt) : IProvedorMarketing
{
    protected ClienteHttpMarketing Http => http;
    public abstract PlataformaMarketing Plataforma { get; }
    public abstract string Fonte { get; }
    public abstract string Escopo { get; }

    public string MontarUrlAutorizacao(string state, string redirectUri, string codeChallenge) =>
        $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Q(opt.GoogleClientId!)}&redirect_uri={Q(redirectUri)}" +
        $"&response_type=code&scope={Q(Escopo)}&access_type=offline&prompt=consent&include_granted_scopes=false" +
        $"&state={Q(state)}&code_challenge={Q(codeChallenge)}&code_challenge_method=S256";

    public async Task<ResultadoToken> TrocarCodigoAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var json = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm("https://oauth2.googleapis.com/token",
        [
            new("code", code), new("client_id", opt.GoogleClientId!), new("client_secret", opt.GoogleClientSecret!),
            new("redirect_uri", redirectUri), new("grant_type", "authorization_code"), new("code_verifier", codeVerifier)
        ]), MapearErro, "google.token", ct);

        var escopos = json.Str("scope") ?? "";
        if (!escopos.Split(' ').Contains(Escopo))
            throw new ErroApiExterna(TipoErroApi.NaoSuportado, "A permissão de leitura não foi concedida na tela do Google. Conecte novamente e marque a permissão solicitada.");

        return new ResultadoToken(json.Str("access_token")!, json.Str("refresh_token"),
            DateTime.UtcNow.AddSeconds(json.Long("expires_in") ?? 3600), null, escopos, null, null);
    }

    public async Task<ResultadoToken?> RenovarAsync(ConexaoMarketing conexao, string accessToken, string? refreshToken, CancellationToken ct)
    {
        if (conexao.ExpiraEm is DateTime exp && exp > DateTime.UtcNow.AddMinutes(5)) return null;
        if (string.IsNullOrEmpty(refreshToken))
            throw new ErroApiExterna(TipoErroApi.TokenInvalido, "Token do Google expirado e sem refresh token. Conecte novamente.");

        var json = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm("https://oauth2.googleapis.com/token",
        [
            new("client_id", opt.GoogleClientId!), new("client_secret", opt.GoogleClientSecret!),
            new("refresh_token", refreshToken), new("grant_type", "refresh_token")
        ]), MapearErro, "google.refresh", ct);

        return new ResultadoToken(json.Str("access_token")!, json.Str("refresh_token") ?? refreshToken,
            DateTime.UtcNow.AddSeconds(json.Long("expires_in") ?? 3600), null, json.Str("scope") ?? conexao.Escopos,
            conexao.UsuarioExternoId, conexao.UsuarioExternoNome);
    }

    public abstract Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct);
    public abstract Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct);

    public async Task<bool> RevogarAsync(string accessToken, CancellationToken ct)
    {
        await http.EnviarAsync(() => ClienteHttpMarketing.PostForm("https://oauth2.googleapis.com/revoke", [new("token", accessToken)]),
            MapearErro, "google.revoke", ct);
        return true;
    }

    internal static ErroApiExterna MapearErro(HttpStatusCode status, string corpo)
    {
        string? statusApi = null, mensagem = null, erroOAuth = null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var err = doc.RootElement.Prop("error");
            if (err.ValueKind == JsonValueKind.Object) { statusApi = err.Str("status"); mensagem = err.Str("message"); }
            else { erroOAuth = doc.RootElement.Str("error"); mensagem = doc.RootElement.Str("error_description"); }
        }
        catch (JsonException) { }

        var tipo = (erroOAuth, statusApi) switch
        {
            ("invalid_grant", _) => TipoErroApi.PermissaoRevogada,
            ("invalid_token", _) => TipoErroApi.TokenInvalido,
            (_, "UNAUTHENTICATED") => TipoErroApi.TokenInvalido,
            (_, "RESOURCE_EXHAUSTED") => TipoErroApi.LimiteTaxa,
            (_, "PERMISSION_DENIED") => TipoErroApi.PermissaoRevogada,
            (_, "INVALID_ARGUMENT" or "FAILED_PRECONDITION") => TipoErroApi.NaoSuportado,
            (_, "UNAVAILABLE" or "INTERNAL" or "DEADLINE_EXCEEDED") => TipoErroApi.Temporario,
            _ when status == HttpStatusCode.TooManyRequests => TipoErroApi.LimiteTaxa,
            _ when status == HttpStatusCode.Unauthorized => TipoErroApi.TokenInvalido,
            _ when status == HttpStatusCode.Forbidden => TipoErroApi.PermissaoRevogada,
            _ when (int)status >= 500 => TipoErroApi.Temporario,
            _ => TipoErroApi.Permanente
        };
        return new ErroApiExterna(tipo, mensagem ?? erroOAuth ?? $"Erro HTTP {(int)status} na API do Google.");
    }

    protected static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Google Analytics 4: Admin API (listar propriedades) + Data API (runReport).</summary>
public class GoogleAnalyticsProvedor(ClienteHttpMarketing http, MarketingOptions opt) : GoogleProvedorBase(http, opt)
{
    public override PlataformaMarketing Plataforma => PlataformaMarketing.GoogleAnalytics;
    public override string Fonte => "Google Analytics Data API";
    public override string Escopo => "https://www.googleapis.com/auth/analytics.readonly";

    internal static readonly (string Api, string Chave)[] MetricasDiarias =
    [
        ("activeUsers", Metricas.UsuariosAtivos),
        ("newUsers", Metricas.NovosUsuarios),
        ("sessions", Metricas.Sessoes),
        ("engagedSessions", Metricas.SessoesEngajadas),
        ("screenPageViews", Metricas.VisualizacoesPagina),
        ("userEngagementDuration", Metricas.TempoEngajamento),
        ("eventCount", Metricas.Eventos),
        ("keyEvents", Metricas.EventosPrincipais),
    ];

    internal static readonly (string DimensaoApi, string Dimensao, (string Api, string Chave)[] Metricas)[] Quebras =
    [
        ("sessionDefaultChannelGroup", Dimensoes.Canal, [("sessions", Metricas.Sessoes), ("activeUsers", Metricas.UsuariosAtivos)]),
        ("sessionSourceMedium", Dimensoes.OrigemMidia, [("sessions", Metricas.Sessoes)]),
        ("sessionCampaignName", Dimensoes.Campanha, [("sessions", Metricas.Sessoes), ("keyEvents", Metricas.EventosPrincipais)]),
        ("pagePath", Dimensoes.Pagina, [("screenPageViews", Metricas.VisualizacoesPagina), ("activeUsers", Metricas.UsuariosAtivos)]),
        ("deviceCategory", Dimensoes.Dispositivo, [("sessions", Metricas.Sessoes), ("activeUsers", Metricas.UsuariosAtivos)]),
        ("country", Dimensoes.Pais, [("sessions", Metricas.Sessoes), ("activeUsers", Metricas.UsuariosAtivos)]),
        ("eventName", Dimensoes.Evento, [("eventCount", Metricas.Eventos), ("keyEvents", Metricas.EventosPrincipais)]),
    ];

    public override async Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct)
    {
        var contas = new List<ContaDisponivel>();
        string? pageToken = null;
        for (var pagina = 0; pagina < 10; pagina++)
        {
            var url = "https://analyticsadmin.googleapis.com/v1beta/accountSummaries?pageSize=200" + (pageToken != null ? $"&pageToken={Q(pageToken)}" : "");
            var json = await Http.EnviarAsync(() => ClienteHttpMarketing.Get(url, accessToken), MapearErro, "ga4.accounts", ct);
            foreach (var conta in json.Itens("accountSummaries"))
            foreach (var prop in conta.Itens("propertySummaries"))
            {
                contas.Add(new ContaDisponivel(prop.Str("property")!, prop.Str("displayName") ?? prop.Str("property")!,
                    conta.Str("displayName"), null, prop.Str("propertyType")));
            }
            pageToken = json.Str("nextPageToken");
            if (string.IsNullOrEmpty(pageToken)) break;
        }
        return contas;
    }

    public override async Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct)
    {
        var dados = new DadosSincronizados();
        var semKeyEvents = false;

        try
        {
            var json = await RelatorioAsync(conta.IdExterno, token, desde, ate, null, MetricasDiarias.Select(m => m.Api), ct);
            LerDiario(json, MetricasDiarias, dados);
        }
        catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado)
        {
            // Propriedades antigas podem não aceitar keyEvents: tenta sem ela
            semKeyEvents = true;
            dados.Indisponiveis.Add(Metricas.EventosPrincipais);
            var metricas = MetricasDiarias.Where(m => m.Api != "keyEvents").ToArray();
            var json = await RelatorioAsync(conta.IdExterno, token, desde, ate, null, metricas.Select(m => m.Api), ct);
            LerDiario(json, metricas, dados);
        }

        foreach (var (dimApi, dimensao, metricas) in Quebras)
        {
            var lista = semKeyEvents ? metricas.Where(m => m.Api != "keyEvents").ToArray() : metricas;
            var json = await RelatorioAsync(conta.IdExterno, token, desde, ate, dimApi, lista.Select(m => m.Api), ct);
            LerQuebra(json, dimensao, lista, dados);
        }
        return dados;
    }

    private Task<JsonElement> RelatorioAsync(string propriedade, string token, DateOnly desde, DateOnly ate, string? dimensao, IEnumerable<string> metricas, CancellationToken ct)
    {
        var dims = new List<object> { new { name = "date" } };
        if (dimensao != null) dims.Add(new { name = dimensao });
        var metricasLista = metricas.ToList();
        var corpo = new
        {
            dateRanges = new[] { new { startDate = Iso(desde), endDate = Iso(ate) } },
            dimensions = dims,
            metrics = metricasLista.Select(m => new { name = m }),
            limit = 10000,
            orderBys = new[] { new { metric = new { metricName = metricasLista[0] }, desc = true } }
        };
        return Http.EnviarAsync(() => ClienteHttpMarketing.PostJson(
            $"https://analyticsdata.googleapis.com/v1beta/{propriedade}:runReport", corpo, token), MapearErro, "ga4.runReport", ct);
    }

    internal static void LerDiario(JsonElement json, (string Api, string Chave)[] metricas, DadosSincronizados dados)
    {
        foreach (var row in json.Itens("rows"))
        {
            if (!TentarData(row.Itens("dimensionValues").First().Str("value"), out var data)) continue;
            var valores = row.Itens("metricValues").ToList();
            for (var i = 0; i < metricas.Length && i < valores.Count; i++)
                dados.Adicionar(data, metricas[i].Chave, ParseDec(valores[i].Prop("value")));
        }
    }

    internal static void LerQuebra(JsonElement json, string dimensao, (string Api, string Chave)[] metricas, DadosSincronizados dados)
    {
        foreach (var row in json.Itens("rows"))
        {
            var dims = row.Itens("dimensionValues").ToList();
            if (dims.Count < 2 || !TentarData(dims[0].Str("value"), out var data)) continue;
            var valorDim = dims[1].Str("value") ?? "(not set)";
            var valores = row.Itens("metricValues").ToList();
            for (var i = 0; i < metricas.Length && i < valores.Count; i++)
                if (ParseDec(valores[i].Prop("value")) is decimal v)
                    dados.Dimensoes.Add(new DimensaoColetada(data, dimensao, Truncar(valorDim), metricas[i].Chave, v));
        }
    }

    private static bool TentarData(string? yyyymmdd, out DateOnly data) =>
        DateOnly.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out data);

    internal static string Truncar(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>Google Search Console: lista de sites e searchAnalytics.query.</summary>
public class SearchConsoleProvedor(ClienteHttpMarketing http, MarketingOptions opt) : GoogleProvedorBase(http, opt)
{
    public override PlataformaMarketing Plataforma => PlataformaMarketing.SearchConsole;
    public override string Fonte => "Google Search Console API";
    public override string Escopo => "https://www.googleapis.com/auth/webmasters.readonly";

    internal static readonly (string Api, string Dimensao, int Limite)[] Quebras =
    [
        ("query", Dimensoes.PalavraChave, 5000),
        ("page", Dimensoes.Pagina, 5000),
        ("device", Dimensoes.Dispositivo, 1000),
        ("country", Dimensoes.Pais, 2000),
    ];

    public override async Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct)
    {
        var json = await Http.EnviarAsync(() => ClienteHttpMarketing.Get("https://www.googleapis.com/webmasters/v3/sites", accessToken),
            MapearErro, "gsc.sites", ct);
        return json.Itens("siteEntry")
            .Where(s => s.Str("permissionLevel") != "siteUnverifiedUser")
            .Select(s => new ContaDisponivel(s.Str("siteUrl")!, s.Str("siteUrl")!.Replace("sc-domain:", "Domínio: "), null, null, s.Str("permissionLevel")))
            .ToList();
    }

    public override async Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct)
    {
        var dados = new DadosSincronizados
        {
            Limitacao = "O Search Console disponibiliza os dados com 2 a 3 dias de atraso."
        };

        var diario = await ConsultarAsync(conta.IdExterno, token, desde, ate, ["date"], 25000, ct);
        foreach (var row in diario.Itens("rows"))
        {
            if (!DateOnly.TryParse(row.Itens("keys").First().GetString(), CultureInfo.InvariantCulture, out var data)) continue;
            var (cliques, impressoes, posicao) = (row.Dec("clicks"), row.Dec("impressions"), row.Dec("position"));
            dados.Adicionar(data, Metricas.CliquesOrganicos, cliques);
            dados.Adicionar(data, Metricas.ImpressoesOrganicas, impressoes);
            dados.Adicionar(data, Metricas.PosicaoPonderada, posicao * impressoes);
        }

        foreach (var (api, dimensao, limite) in Quebras)
        {
            var json = await ConsultarAsync(conta.IdExterno, token, desde, ate, ["date", api], limite, ct);
            LerQuebra(json, dimensao, dados);
        }
        return dados;
    }

    internal static void LerQuebra(JsonElement json, string dimensao, DadosSincronizados dados)
    {
        foreach (var row in json.Itens("rows"))
        {
            var keys = row.Itens("keys").Select(k => k.GetString() ?? "").ToList();
            if (keys.Count < 2 || !DateOnly.TryParse(keys[0], CultureInfo.InvariantCulture, out var data)) continue;
            var valor = GoogleAnalyticsProvedor.Truncar(keys[1]);
            var (cliques, impressoes, posicao) = (row.Dec("clicks") ?? 0, row.Dec("impressions") ?? 0, row.Dec("position") ?? 0);
            dados.Dimensoes.Add(new DimensaoColetada(data, dimensao, valor, Metricas.CliquesOrganicos, cliques));
            dados.Dimensoes.Add(new DimensaoColetada(data, dimensao, valor, Metricas.ImpressoesOrganicas, impressoes));
            dados.Dimensoes.Add(new DimensaoColetada(data, dimensao, valor, Metricas.PosicaoPonderada, posicao * impressoes));
        }
    }

    private Task<JsonElement> ConsultarAsync(string site, string token, DateOnly desde, DateOnly ate, string[] dimensoes, int limite, CancellationToken ct) =>
        Http.EnviarAsync(() => ClienteHttpMarketing.PostJson(
            $"https://www.googleapis.com/webmasters/v3/sites/{Q(site)}/searchAnalytics/query",
            new { startDate = Iso(desde), endDate = Iso(ate), dimensions = dimensoes, rowLimit = limite, type = "web" }, token),
            MapearErro, "gsc.query", ct);
}
