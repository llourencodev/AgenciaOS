using System.Text.Json;
using AgenciaOS.Models;
using static AgenciaOS.Services.Marketing.Provedores.JsonExt;

namespace AgenciaOS.Services.Marketing.Provedores;

/// <summary>
/// Instagram API com Login do Instagram (contas profissionais: comercial ou criador de conteúdo).
/// Escopos mínimos: instagram_business_basic, instagram_business_manage_insights.
/// </summary>
public class InstagramProvedor(ClienteHttpMarketing http, MarketingOptions opt) : IProvedorMarketing
{
    public const string Escopos = "instagram_business_basic,instagram_business_manage_insights";

    public PlataformaMarketing Plataforma => PlataformaMarketing.Instagram;
    public string Fonte => "Instagram Platform API";
    private string Graph => $"https://graph.instagram.com/{opt.InstagramGraphVersion}";

    internal static readonly Dictionary<string, string> MetricasTotal = new()
    {
        ["views"] = Metricas.Visualizacoes,
        ["accounts_engaged"] = Metricas.ContasEngajadas,
        ["total_interactions"] = Metricas.Interacoes,
        ["likes"] = Metricas.Curtidas,
        ["comments"] = Metricas.Comentarios,
        ["shares"] = Metricas.Compartilhamentos,
        ["saves"] = Metricas.Salvamentos,
        ["profile_links_taps"] = Metricas.CliquesLink,
        ["profile_views"] = Metricas.VisitasPerfil,
    };

    public string MontarUrlAutorizacao(string state, string redirectUri, string codeChallenge) =>
        $"https://www.instagram.com/oauth/authorize?enable_fb_login=0&client_id={Q(opt.InstagramAppId!)}" +
        $"&redirect_uri={Q(redirectUri)}&response_type=code&scope={Q(Escopos)}&state={Q(state)}";

    public async Task<ResultadoToken> TrocarCodigoAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var curto = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm("https://api.instagram.com/oauth/access_token",
        [
            new("client_id", opt.InstagramAppId!), new("client_secret", opt.InstagramAppSecret!),
            new("grant_type", "authorization_code"), new("redirect_uri", redirectUri), new("code", code)
        ]), MetaErros.Mapear, "instagram.token", ct);

        // A resposta pode vir plana ou dentro de "data": [...]
        var item = curto.Prop("data").ValueKind == JsonValueKind.Array ? curto.Itens("data").First() : curto;
        var permissoes = item.Prop("permissions");
        var escopos = permissoes.ValueKind == JsonValueKind.Array
            ? string.Join(",", permissoes.EnumerateArray().Select(p => p.GetString()))
            : item.Str("permissions");

        var longo = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"https://graph.instagram.com/access_token?grant_type=ig_exchange_token&client_secret={Q(opt.InstagramAppSecret!)}" +
            $"&access_token={Q(item.Str("access_token")!)}"), MetaErros.Mapear, "instagram.token_longo", ct);

        var token = longo.Str("access_token")!;
        var me = await http.EnviarAsync(() => ClienteHttpMarketing.Get($"{Graph}/me?fields=user_id,username&access_token={Q(token)}"),
            MetaErros.Mapear, "instagram.me", ct);

        return new ResultadoToken(token, null,
            longo.Long("expires_in") is long s ? DateTime.UtcNow.AddSeconds(s) : DateTime.UtcNow.AddDays(60),
            null, escopos, me.Str("user_id") ?? item.Str("user_id"), me.Str("username"));
    }

    public async Task<ResultadoToken?> RenovarAsync(ConexaoMarketing conexao, string accessToken, string? refreshToken, CancellationToken ct)
    {
        // Token de longa duração vale 60 dias e pode ser renovado depois de 24h de emitido.
        if (conexao.ExpiraEm is DateTime exp && exp > DateTime.UtcNow.AddDays(7)) return null;
        if (conexao.AtualizadoEm > DateTime.UtcNow.AddHours(-24)) return null;

        var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token={Q(accessToken)}"),
            MetaErros.Mapear, "instagram.refresh", ct);
        return new ResultadoToken(json.Str("access_token")!, null,
            json.Long("expires_in") is long s ? DateTime.UtcNow.AddSeconds(s) : DateTime.UtcNow.AddDays(60),
            null, conexao.Escopos, conexao.UsuarioExternoId, conexao.UsuarioExternoNome);
    }

    public async Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct)
    {
        var me = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"{Graph}/me?fields=user_id,username,name,profile_picture_url,account_type&access_token={Q(accessToken)}"),
            MetaErros.Mapear, "instagram.me", ct);
        var tipo = me.Str("account_type");
        return
        [
            new ContaDisponivel(me.Str("user_id")!, me.Str("name") ?? me.Str("username") ?? "Instagram",
                me.Str("username"), me.Str("profile_picture_url"), tipo, AvisoTipoConta(tipo))
        ];
    }

    internal static string? AvisoTipoConta(string? tipo) =>
        tipo is null or "BUSINESS" or "MEDIA_CREATOR" ? null :
        "Esta conta não é profissional. A API do Instagram só fornece métricas (insights) de contas comerciais ou de criador de conteúdo. " +
        "No app do Instagram, acesse Configurações > Tipo de conta e ferramentas > Mudar para conta profissional e conecte novamente.";

    public async Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct)
    {
        var dados = new DadosSincronizados();
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        var perfil = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"{Graph}/me?fields=user_id,username,name,profile_picture_url,followers_count,media_count,account_type&access_token={Q(token)}"),
            MetaErros.Mapear, "instagram.perfil", ct);
        dados.Nome = perfil.Str("name") ?? perfil.Str("username");
        dados.NomeUsuario = perfil.Str("username");
        dados.FotoUrl = perfil.Str("profile_picture_url");
        dados.TipoConta = perfil.Str("account_type");
        dados.Adicionar(hoje, Metricas.Seguidores, perfil.Dec("followers_count"));
        dados.Adicionar(hoje, Metricas.Publicacoes, perfil.Dec("media_count"));

        if (AvisoTipoConta(dados.TipoConta) is string aviso)
        {
            dados.Limitacao = aviso;
            return dados;
        }

        // A API de insights de conta só aceita até 30 dias por consulta
        if (ate.DayNumber - desde.DayNumber > 29) desde = ate.AddDays(-29);

        await SerieTemporalAsync(conta.IdExterno, token, "reach", Metricas.Alcance, desde, ate, dados, ct);
        await SerieTemporalAsync(conta.IdExterno, token, "follower_count", Metricas.NovosSeguidores, desde, ate, dados, ct);
        await TotaisDiariosAsync(conta.IdExterno, token, desde, ate, dados, ct);
        await PublicacoesAsync(conta.IdExterno, token, dados, ct);
        return dados;
    }

    private async Task SerieTemporalAsync(string id, string token, string metricaApi, string chave, DateOnly desde, DateOnly ate, DadosSincronizados dados, CancellationToken ct)
    {
        try
        {
            var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
                $"{Graph}/{id}/insights?metric={metricaApi}&period=day&since={DiaInicio(desde)}&until={DiaInicio(ate.AddDays(1))}&access_token={Q(token)}"),
                MetaErros.Mapear, "instagram.insights_serie", ct);
            foreach (var (data, valor) in FacebookProvedor.LerSerieInsights(json))
                dados.Adicionar(data, chave, valor);
        }
        catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado)
        {
            dados.Indisponiveis.Add(chave);
        }
    }

    private async Task TotaisDiariosAsync(string id, string token, DateOnly desde, DateOnly ate, DadosSincronizados dados, CancellationToken ct)
    {
        // Descobre, no dia mais recente, quais métricas a conta suporta (evita perder o dia inteiro por uma métrica inválida)
        var suportadas = new List<string>();
        foreach (var m in MetricasTotal.Keys)
        {
            try
            {
                var json = await ConsultarTotalAsync(id, token, [m], ate, ct);
                LerTotais(json, ate, dados);
                suportadas.Add(m);
            }
            catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado)
            {
                dados.Indisponiveis.Add(MetricasTotal[m]);
            }
        }
        if (suportadas.Count == 0) return;

        for (var dia = desde; dia < ate; dia = dia.AddDays(1))
        {
            var json = await ConsultarTotalAsync(id, token, suportadas, dia, ct);
            LerTotais(json, dia, dados);
        }
    }

    private Task<JsonElement> ConsultarTotalAsync(string id, string token, IEnumerable<string> metricas, DateOnly dia, CancellationToken ct) =>
        http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"{Graph}/{id}/insights?metric={string.Join(",", metricas)}&period=day&metric_type=total_value" +
            $"&since={DiaInicio(dia)}&until={DiaInicio(dia.AddDays(1))}&access_token={Q(token)}"),
            MetaErros.Mapear, "instagram.insights_total", ct);

    internal static void LerTotais(JsonElement json, DateOnly dia, DadosSincronizados dados)
    {
        foreach (var m in json.Itens("data"))
        {
            if (m.Str("name") is string nome && MetricasTotal.TryGetValue(nome, out var chave))
                dados.Adicionar(dia, chave, m.Prop("total_value").Dec("value"));
        }
    }

    private async Task PublicacoesAsync(string id, string token, DadosSincronizados dados, CancellationToken ct)
    {
        var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
            $"{Graph}/{id}/media?fields=id,caption,media_type,media_product_type,media_url,thumbnail_url,permalink,timestamp,like_count,comments_count" +
            $"&limit=25&access_token={Q(token)}"), MetaErros.Mapear, "instagram.media", ct);

        foreach (var m in json.Itens("data"))
        {
            var tipo = m.Str("media_product_type") == "REELS" ? "REELS" : m.Str("media_type");
            var pub = new PublicacaoColetada
            {
                IdExterno = m.Str("id")!,
                Tipo = tipo,
                Legenda = m.Str("caption"),
                Link = m.Str("permalink"),
                ThumbnailUrl = m.Str("thumbnail_url") ?? (m.Str("media_type") == "VIDEO" ? null : m.Str("media_url")),
                PublicadoEm = DateTimeOffset.TryParse(m.Str("timestamp"), out var dt) ? dt.UtcDateTime : DateTime.UtcNow,
                Curtidas = m.Long("like_count"),
                Comentarios = m.Long("comments_count"),
            };

            // Métricas variam por tipo de mídia; tenta o conjunto completo e depois um conjunto básico
            foreach (var conjunto in new[] { "reach,views,likes,comments,shares,saved,total_interactions", "reach,likes,comments,shares,saved" })
            {
                try
                {
                    var ins = await http.EnviarAsync(() => ClienteHttpMarketing.Get(
                        $"{Graph}/{pub.IdExterno}/insights?metric={conjunto}&access_token={Q(token)}"),
                        MetaErros.Mapear, "instagram.media_insights", ct);
                    LerInsightsMidia(ins, pub);
                    break;
                }
                catch (ErroApiExterna e) when (e.Tipo == TipoErroApi.NaoSuportado) { }
            }
            pub.Interacoes ??= (pub.Curtidas ?? 0) + (pub.Comentarios ?? 0) + (pub.Compartilhamentos ?? 0) + (pub.Salvamentos ?? 0);
            dados.Publicacoes.Add(pub);
        }
    }

    internal static void LerInsightsMidia(JsonElement ins, PublicacaoColetada pub)
    {
        foreach (var i in ins.Itens("data"))
        {
            var valor = i.Itens("values").Select(v => (long?)ParseDec(v.Prop("value"))).FirstOrDefault()
                        ?? (long?)i.Prop("total_value").Dec("value");
            switch (i.Str("name"))
            {
                case "reach": pub.Alcance = valor; break;
                case "views": pub.Visualizacoes = valor; break;
                case "likes": pub.Curtidas = valor ?? pub.Curtidas; break;
                case "comments": pub.Comentarios = valor ?? pub.Comentarios; break;
                case "shares": pub.Compartilhamentos = valor; break;
                case "saved": pub.Salvamentos = valor; break;
                case "total_interactions": pub.Interacoes = valor; break;
            }
        }
    }

    public Task<bool> RevogarAsync(string accessToken, CancellationToken ct) =>
        // A Instagram API não oferece endpoint de revogação: o usuário remove o app em
        // Configurações > Apps e sites. Localmente o token é apagado.
        Task.FromResult(false);

    /// <summary>Início do dia no horário do Pacífico (fuso usado pela Meta nos insights).</summary>
    private static long DiaInicio(DateOnly d) => FacebookProvedor.Unix(d) + 8 * 3600;
}
