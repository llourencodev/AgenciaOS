using System.Net;
using System.Text.Json;
using AgenciaOS.Models;
using static AgenciaOS.Services.Marketing.Provedores.JsonExt;

namespace AgenciaOS.Services.Marketing.Provedores;

/// <summary>
/// TikTok for Developers: Login Kit (OAuth v2) + Display API (user/info e video/list).
/// Os campos retornados dependem dos escopos aprovados para o app.
/// </summary>
public class TikTokProvedor(ClienteHttpMarketing http, MarketingOptions opt) : IProvedorMarketing
{
    public const string Escopos = "user.info.basic,user.info.profile,user.info.stats,video.list";
    private const string Api = "https://open.tiktokapis.com/v2";

    public PlataformaMarketing Plataforma => PlataformaMarketing.TikTok;
    public string Fonte => "TikTok Display API";

    public string MontarUrlAutorizacao(string state, string redirectUri, string codeChallenge) =>
        $"https://www.tiktok.com/v2/auth/authorize/?client_key={Q(opt.TikTokClientKey!)}" +
        $"&scope={Q(Escopos)}&response_type=code&redirect_uri={Q(redirectUri)}&state={Q(state)}";

    public async Task<ResultadoToken> TrocarCodigoAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var json = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm($"{Api}/oauth/token/",
        [
            new("client_key", opt.TikTokClientKey!), new("client_secret", opt.TikTokClientSecret!),
            new("code", code), new("grant_type", "authorization_code"), new("redirect_uri", redirectUri)
        ]), MapearErro, "tiktok.token", ct);
        return LerToken(json, null);
    }

    public async Task<ResultadoToken?> RenovarAsync(ConexaoMarketing conexao, string accessToken, string? refreshToken, CancellationToken ct)
    {
        // Access token dura 24h; refresh token, 365 dias.
        if (conexao.ExpiraEm is DateTime exp && exp > DateTime.UtcNow.AddMinutes(30)) return null;
        if (string.IsNullOrEmpty(refreshToken))
            throw new ErroApiExterna(TipoErroApi.TokenInvalido, "Token do TikTok expirado e sem refresh token.");

        var json = await http.EnviarAsync(() => ClienteHttpMarketing.PostForm($"{Api}/oauth/token/",
        [
            new("client_key", opt.TikTokClientKey!), new("client_secret", opt.TikTokClientSecret!),
            new("grant_type", "refresh_token"), new("refresh_token", refreshToken)
        ]), MapearErro, "tiktok.refresh", ct);
        return LerToken(json, conexao.UsuarioExternoNome);
    }

    private static ResultadoToken LerToken(JsonElement json, string? nome)
    {
        var agora = DateTime.UtcNow;
        return new ResultadoToken(
            json.Str("access_token") ?? throw new ErroApiExterna(TipoErroApi.Permanente, "Resposta de token do TikTok sem access_token."),
            json.Str("refresh_token"),
            json.Long("expires_in") is long e ? agora.AddSeconds(e) : agora.AddHours(24),
            json.Long("refresh_expires_in") is long r ? agora.AddSeconds(r) : null,
            json.Str("scope"),
            json.Str("open_id"),
            nome);
    }

    public async Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct)
    {
        var user = await UsuarioAsync(accessToken, "open_id,avatar_url,display_name,username", ct);
        return [new ContaDisponivel(user.Str("open_id")!, user.Str("display_name") ?? "TikTok", user.Str("username"), user.Str("avatar_url"))];
    }

    private async Task<JsonElement> UsuarioAsync(string token, string campos, CancellationToken ct)
    {
        var json = await http.EnviarAsync(() => ClienteHttpMarketing.Get($"{Api}/user/info/?fields={campos}", token),
            MapearErro, "tiktok.user", ct);
        return json.Prop("data").Prop("user");
    }

    public async Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct)
    {
        var dados = new DadosSincronizados();
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var escopos = conta.Conexao?.Escopos ?? "";

        var campos = "open_id,avatar_url,display_name";
        if (escopos.Contains("user.info.profile")) campos += ",username";
        if (escopos.Contains("user.info.stats")) campos += ",follower_count,likes_count,video_count";
        else dados.Indisponiveis.UnionWith([Metricas.Seguidores, Metricas.CurtidasTotais, Metricas.Publicacoes]);

        var user = await UsuarioAsync(token, campos, ct);
        dados.Nome = user.Str("display_name");
        dados.NomeUsuario = user.Str("username");
        dados.FotoUrl = user.Str("avatar_url");
        dados.Adicionar(hoje, Metricas.Seguidores, user.Dec("follower_count"));
        dados.Adicionar(hoje, Metricas.CurtidasTotais, user.Dec("likes_count"));
        dados.Adicionar(hoje, Metricas.Publicacoes, user.Dec("video_count"));

        dados.Limitacao = "A API do TikTok fornece totais acumulados por perfil e por vídeo. Não há série diária de alcance ou visualizações do perfil; " +
                          "a evolução de seguidores é montada a partir das sincronizações.";

        if (!escopos.Contains("video.list"))
        {
            dados.Indisponiveis.Add(Metricas.Visualizacoes);
            return dados;
        }

        long? cursor = null;
        for (var pagina = 0; pagina < 2; pagina++)
        {
            object corpo = cursor is long c ? new { max_count = 20, cursor = c } : new { max_count = 20 };
            var json = await http.EnviarAsync(() => ClienteHttpMarketing.PostJson(
                $"{Api}/video/list/?fields=id,title,video_description,create_time,cover_image_url,share_url,view_count,like_count,comment_count,share_count",
                corpo, token), MapearErro, "tiktok.videos", ct);
            var data = json.Prop("data");
            foreach (var v in data.Itens("videos"))
            {
                var pub = new PublicacaoColetada
                {
                    IdExterno = v.Str("id")!,
                    Tipo = "VIDEO",
                    Legenda = v.Str("video_description") ?? v.Str("title"),
                    Link = v.Str("share_url"),
                    ThumbnailUrl = v.Str("cover_image_url"),
                    PublicadoEm = v.Long("create_time") is long t ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : DateTime.UtcNow,
                    Visualizacoes = v.Long("view_count"),
                    Curtidas = v.Long("like_count"),
                    Comentarios = v.Long("comment_count"),
                    Compartilhamentos = v.Long("share_count"),
                };
                pub.Interacoes = (pub.Curtidas ?? 0) + (pub.Comentarios ?? 0) + (pub.Compartilhamentos ?? 0);
                dados.Publicacoes.Add(pub);
            }
            if (data.Prop("has_more").ValueKind != JsonValueKind.True) break;
            cursor = data.Long("cursor");
        }
        return dados;
    }

    public async Task<bool> RevogarAsync(string accessToken, CancellationToken ct)
    {
        await http.EnviarAsync(() => ClienteHttpMarketing.PostForm($"{Api}/oauth/revoke/",
        [
            new("client_key", opt.TikTokClientKey!), new("client_secret", opt.TikTokClientSecret!), new("token", accessToken)
        ]), MapearErro, "tiktok.revoke", ct);
        return true;
    }

    internal static ErroApiExterna MapearErro(HttpStatusCode status, string corpo)
    {
        string? code = null, mensagem = null;
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var err = doc.RootElement.Prop("error");
            if (err.ValueKind == JsonValueKind.Object) { code = err.Str("code"); mensagem = err.Str("message"); }
            else { code = doc.RootElement.Str("error"); mensagem = doc.RootElement.Str("error_description"); }
        }
        catch (JsonException) { }

        var tipo = code switch
        {
            "access_token_invalid" or "invalid_grant" => TipoErroApi.TokenInvalido,
            "scope_not_authorized" or "scope_permission_missed" => TipoErroApi.NaoSuportado,
            "rate_limit_exceeded" => TipoErroApi.LimiteTaxa,
            "internal_error" => TipoErroApi.Temporario,
            _ when status == HttpStatusCode.TooManyRequests => TipoErroApi.LimiteTaxa,
            _ when status == HttpStatusCode.Unauthorized => TipoErroApi.TokenInvalido,
            _ when (int)status >= 500 => TipoErroApi.Temporario,
            _ => TipoErroApi.Permanente
        };
        return new ErroApiExterna(tipo, mensagem ?? $"Erro HTTP {(int)status} na API do TikTok ({code}).");
    }
}
