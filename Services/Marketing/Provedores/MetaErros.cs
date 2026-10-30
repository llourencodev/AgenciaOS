using System.Net;
using System.Text.Json;

namespace AgenciaOS.Services.Marketing.Provedores;

/// <summary>Converte erros da Graph API (Facebook e Instagram) em ErroApiExterna.</summary>
public static class MetaErros
{
    public static ErroApiExterna Mapear(HttpStatusCode status, string corpo)
    {
        int? code = null, subcode = null;
        string mensagem = $"Erro HTTP {(int)status} na API da Meta.";
        try
        {
            using var doc = JsonDocument.Parse(corpo);
            var err = doc.RootElement.Prop("error");
            if (err.ValueKind == JsonValueKind.Object)
            {
                code = (int?)err.Long("code");
                subcode = (int?)err.Long("error_subcode");
                mensagem = err.Str("message") ?? mensagem;
            }
            else if (doc.RootElement.Str("error_message") is string m)
            {
                // formato do endpoint de token do Instagram
                mensagem = m;
                code = (int?)doc.RootElement.Long("code");
            }
        }
        catch (JsonException) { }

        var tipo = (code, subcode) switch
        {
            (190, 458 or 459 or 460) => TipoErroApi.PermissaoRevogada,
            (190, _) => TipoErroApi.TokenInvalido,
            (102, _) => TipoErroApi.TokenInvalido,
            (10, _) => TipoErroApi.NaoSuportado,
            (>= 200 and <= 299, _) => TipoErroApi.NaoSuportado,
            (4 or 17 or 32 or 613 or 80001 or 80002 or 80004 or 80005 or 80006 or 80008, _) => TipoErroApi.LimiteTaxa,
            (1 or 2, _) => TipoErroApi.Temporario,
            (100, _) => TipoErroApi.NaoSuportado,
            _ when status == HttpStatusCode.TooManyRequests => TipoErroApi.LimiteTaxa,
            _ when (int)status >= 500 => TipoErroApi.Temporario,
            _ when status == HttpStatusCode.Unauthorized => TipoErroApi.TokenInvalido,
            _ => TipoErroApi.Permanente
        };
        return new ErroApiExterna(tipo, mensagem);
    }
}
