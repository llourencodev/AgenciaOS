using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenciaOS.Models;

namespace AgenciaOS.Services.Marketing.Provedores;

public enum TipoErroApi
{
    /// <summary>Token expirado ou inválido; refresh pode resolver.</summary>
    TokenInvalido,
    /// <summary>Usuário revogou o acesso ou removeu o app.</summary>
    PermissaoRevogada,
    /// <summary>Permissão/escopo não concedido ou recurso não suportado para a conta.</summary>
    NaoSuportado,
    LimiteTaxa,
    Temporario,
    Permanente
}

public class ErroApiExterna(TipoErroApi tipo, string mensagem, TimeSpan? tentarApos = null) : Exception(Sanitizador.Limpar(mensagem))
{
    public TipoErroApi Tipo { get; } = tipo;
    public TimeSpan? TentarApos { get; } = tentarApos;
}

public static partial class Sanitizador
{
    // Remove qualquer coisa que pareça token antes de gravar ou logar mensagens.
    [GeneratedRegex(@"(access_token|refresh_token|client_secret|code|token)=[^&\s""]+", RegexOptions.IgnoreCase)]
    private static partial Regex ParametrosSensiveis();

    [GeneratedRegex(@"\b(EAA|IG|ya29\.|act\.|1//)[A-Za-z0-9._\-]{16,}")]
    private static partial Regex TokensConhecidos();

    public static string Limpar(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        var t = ParametrosSensiveis().Replace(texto, "$1=***");
        t = TokensConhecidos().Replace(t, "***");
        return t.Length > 500 ? t[..500] : t;
    }
}

public record ResultadoToken(
    string AccessToken,
    string? RefreshToken,
    DateTime? ExpiraEm,
    DateTime? RefreshExpiraEm,
    string? Escopos,
    string? UsuarioExternoId,
    string? UsuarioExternoNome);

/// <summary>Conta/página/propriedade que o usuário pode vincular depois do OAuth.</summary>
public record ContaDisponivel(
    string IdExterno,
    string Nome,
    string? NomeUsuario = null,
    string? FotoUrl = null,
    string? TipoConta = null,
    string? Aviso = null)
{
    /// <summary>Token do recurso (ex.: página do Facebook). Nunca enviado para a view.</summary>
    public string? TokenRecurso { get; init; }
}

public record MetricaColetada(DateOnly Data, string Metrica, decimal Valor);
public record DimensaoColetada(DateOnly Data, string Dimensao, string ValorDimensao, string Metrica, decimal Valor);

public class PublicacaoColetada
{
    public required string IdExterno { get; init; }
    public string? Tipo { get; init; }
    public string? Legenda { get; init; }
    public string? Link { get; init; }
    public string? ThumbnailUrl { get; init; }
    public DateTime PublicadoEm { get; init; }
    public long? Visualizacoes { get; set; }
    public long? Alcance { get; set; }
    public long? Curtidas { get; set; }
    public long? Comentarios { get; set; }
    public long? Compartilhamentos { get; set; }
    public long? Salvamentos { get; set; }
    public long? Cliques { get; set; }
    public long? Interacoes { get; set; }
}

public class DadosSincronizados
{
    public List<MetricaColetada> Metricas { get; } = [];
    public List<DimensaoColetada> Dimensoes { get; } = [];
    public List<PublicacaoColetada> Publicacoes { get; } = [];
    public HashSet<string> Indisponiveis { get; } = [];
    public string? Limitacao { get; set; }

    public string? Nome { get; set; }
    public string? NomeUsuario { get; set; }
    public string? FotoUrl { get; set; }
    public string? TipoConta { get; set; }

    public void Adicionar(DateOnly data, string metrica, decimal? valor)
    {
        if (valor is decimal v) Metricas.Add(new MetricaColetada(data, metrica, v));
    }
}

public interface IProvedorMarketing
{
    PlataformaMarketing Plataforma { get; }
    string Fonte { get; }
    string MontarUrlAutorizacao(string state, string redirectUri, string codeChallenge);
    Task<ResultadoToken> TrocarCodigoAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct);
    /// <summary>Renova o token, se o provedor suportar. Retorna null se não for necessário/suportado.</summary>
    Task<ResultadoToken?> RenovarAsync(ConexaoMarketing conexao, string accessToken, string? refreshToken, CancellationToken ct);
    Task<List<ContaDisponivel>> ListarContasAsync(string accessToken, CancellationToken ct);
    Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly desde, DateOnly ate, bool inicial, CancellationToken ct);
    /// <summary>Revoga o acesso no provedor (melhor esforço). Retorna false se o provedor não oferecer revogação por API.</summary>
    Task<bool> RevogarAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// HttpClient compartilhado pelos provedores: retentativas com backoff para 429/5xx,
/// sem log de URLs (que podem conter tokens) e com mapeamento de erros por provedor.
/// </summary>
public class ClienteHttpMarketing(IHttpClientFactory factory, ILogger<ClienteHttpMarketing> logger)
{
    public const string NomeCliente = "marketing";
    private const int MaxTentativas = 3;

    public async Task<JsonElement> EnviarAsync(
        Func<HttpRequestMessage> criarRequisicao,
        Func<HttpStatusCode, string, ErroApiExterna> mapearErro,
        string operacao,
        CancellationToken ct)
    {
        var http = factory.CreateClient(NomeCliente);
        for (var tentativa = 1; ; tentativa++)
        {
            using var req = criarRequisicao();
            HttpResponseMessage resp;
            try
            {
                resp = await http.SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (tentativa >= MaxTentativas)
                    throw new ErroApiExterna(TipoErroApi.Temporario, $"Falha de rede em {operacao}.");
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, tentativa)), ct);
                continue;
            }

            using (resp)
            {
                var corpo = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    if (string.IsNullOrWhiteSpace(corpo)) return default;
                    using var doc = JsonDocument.Parse(corpo);
                    return doc.RootElement.Clone();
                }

                var erro = mapearErro(resp.StatusCode, corpo);
                var retryAfter = resp.Headers.RetryAfter?.Delta;
                var podeRepetir = erro.Tipo is TipoErroApi.Temporario
                    || (erro.Tipo == TipoErroApi.LimiteTaxa && (retryAfter ?? TimeSpan.Zero) <= TimeSpan.FromSeconds(30));

                logger.LogWarning("Marketing API {Operacao} falhou: HTTP {Status} ({Tipo}), tentativa {Tentativa}",
                    operacao, (int)resp.StatusCode, erro.Tipo, tentativa);

                if (!podeRepetir || tentativa >= MaxTentativas)
                {
                    if (erro.Tipo == TipoErroApi.LimiteTaxa && retryAfter != null)
                        throw new ErroApiExterna(erro.Tipo, erro.Message, retryAfter);
                    throw erro;
                }
                await Task.Delay(retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, tentativa)), ct);
            }
        }
    }

    public static HttpRequestMessage Get(string url, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    public static HttpRequestMessage PostForm(string url, IEnumerable<KeyValuePair<string, string>> campos, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(campos) };
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    public static HttpRequestMessage PostJson(string url, object corpo, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json")
        };
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }
}

internal static class JsonExt
{
    public static string? Str(this JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind != JsonValueKind.Null
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;

    public static long? Long(this JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), out var s) ? s : null,
            _ => null
        };
    }

    public static decimal? Dec(this JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        return ParseDec(v);
    }

    public static decimal? ParseDec(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDecimal(),
        JsonValueKind.String => decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
        _ => null
    };

    public static JsonElement Prop(this JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) ? v : default;

    public static IEnumerable<JsonElement> Itens(this JsonElement e, string nome)
    {
        var arr = e.Prop(nome);
        return arr.ValueKind == JsonValueKind.Array ? arr.EnumerateArray() : [];
    }

    public static string Q(string v) => Uri.EscapeDataString(v);
}
