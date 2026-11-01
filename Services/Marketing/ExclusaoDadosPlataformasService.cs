using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing.Provedores;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Exclusão de dados e desautorização exigidas pelas plataformas:
/// - Meta (Facebook e Instagram): callbacks de exclusão de dados e de desautorização com <c>signed_request</c> (HMAC-SHA256 com o app secret).
/// - TikTok: webhook <c>authorization.removed</c> assinado (cabeçalho TikTok-Signature).
/// - Google e todas as outras: botão "Excluir dados" na tela de integrações e revogação ao desconectar.
/// </summary>
public class ExclusaoDadosPlataformasService(
    ApplicationDbContext db,
    RegistroProvedores provedores,
    ProtetorTokens protetor,
    MarketingOptions opt,
    TenantContext tenant,
    ILogger<ExclusaoDadosPlataformasService> logger)
{
    /// <summary>Apaga a conexão e, em cascata, contas, métricas, publicações e histórico obtidos da plataforma.</summary>
    public async Task ExcluirConexaoAsync(ConexaoMarketing conexao, bool revogar, CancellationToken ct = default)
    {
        if (revogar && !string.IsNullOrEmpty(conexao.TokenAcessoProtegido))
        {
            try { await provedores.Obter(conexao.Plataforma).RevogarAsync(protetor.Revelar(conexao.TokenAcessoProtegido), ct); }
            catch (Exception e) { logger.LogWarning("Revogação no provedor {Plataforma} falhou: {Erro}", conexao.Plataforma, Sanitizador.Limpar(e.Message)); }
        }
        db.ConexoesMarketing.Remove(conexao);
        await db.SaveChangesAsync(ct);
    }

    // ── Meta ────────────────────────────────────────────────────────────
    /// <summary>Valida e decodifica o signed_request da Meta. null se a assinatura não confere com nenhum app secret configurado.</summary>
    public JsonElement? LerSignedRequestMeta(string? signedRequest)
    {
        if (string.IsNullOrWhiteSpace(signedRequest)) return null;
        var partes = signedRequest.Split('.', 2);
        if (partes.Length != 2) return null;
        byte[] assinatura, payload;
        try
        {
            assinatura = WebEncoders.Base64UrlDecode(partes[0]);
            payload = WebEncoders.Base64UrlDecode(partes[1]);
        }
        catch (FormatException) { return null; }

        foreach (var segredo in new[] { opt.MetaAppSecret, opt.InstagramAppSecret }.Where(s => !string.IsNullOrEmpty(s)))
        {
            var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo!), Encoding.UTF8.GetBytes(partes[1]));
            if (!CryptographicOperations.FixedTimeEquals(esperado, assinatura)) continue;
            try
            {
                var json = JsonDocument.Parse(payload).RootElement.Clone();
                if (!json.TryGetProperty("algorithm", out var alg) || !string.Equals(alg.GetString(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
                    return null;
                return json;
            }
            catch (JsonException) { return null; }
        }
        return null;
    }

    private static string? UsuarioDoSignedRequest(JsonElement json) =>
        json.TryGetProperty("user_id", out var u) ? u.ValueKind == JsonValueKind.Number ? u.GetRawText() : u.GetString() : null;

    /// <summary>Pedido de exclusão vindo da Meta: apaga tudo do usuário e devolve o código de confirmação (protocolo).</summary>
    public async Task<(string Codigo, int Conexoes)?> ExclusaoMetaAsync(string? signedRequest, CancellationToken ct = default)
    {
        if (LerSignedRequestMeta(signedRequest) is not { } json || UsuarioDoSignedRequest(json) is not { } usuarioExterno) return null;
        tenant.UsarSistema();

        var conexoes = await db.ConexoesMarketing
            .Where(c => (c.Plataforma == PlataformaMarketing.Facebook || c.Plataforma == PlataformaMarketing.Instagram) && c.UsuarioExternoId == usuarioExterno)
            .ToListAsync(ct);
        db.ConexoesMarketing.RemoveRange(conexoes); // a pessoa já removeu o app: não há o que revogar
        var codigo = $"META-{DateTime.UtcNow:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}"; // cabe nos 20 caracteres do protocolo
        db.SolicitacoesPrivacidade.Add(new SolicitacaoPrivacidade
        {
            Protocolo = codigo, NomeTitular = "Usuário Meta", EmailTitular = "", Tipo = TipoSolicitacaoPrivacidade.Exclusao,
            Descricao = $"Exclusão solicitada pela Meta (callback de exclusão de dados). Conexões removidas: {conexoes.Count}.",
            Status = StatusSolicitacaoPrivacidade.Concluida, ConcluidaEm = DateTime.UtcNow, Resposta = "Dados obtidos da Meta excluídos automaticamente."
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Exclusão de dados Meta {Codigo}: {N} conexões removidas", codigo, conexoes.Count);
        return (codigo, conexoes.Count);
    }

    /// <summary>App removido pelo usuário na Meta: apaga os tokens e marca a permissão como revogada.</summary>
    public async Task<int?> DesautorizacaoMetaAsync(string? signedRequest, CancellationToken ct = default)
    {
        if (LerSignedRequestMeta(signedRequest) is not { } json || UsuarioDoSignedRequest(json) is not { } usuarioExterno) return null;
        return await MarcarRevogadasAsync(c => (c.Plataforma == PlataformaMarketing.Facebook || c.Plataforma == PlataformaMarketing.Instagram)
                                               && c.UsuarioExternoId == usuarioExterno, ct);
    }

    // ── TikTok ──────────────────────────────────────────────────────────
    /// <summary>
    /// Confere o cabeçalho TikTok-Signature ("t=timestamp,s=assinatura"), onde assinatura = HMAC-SHA256(client_secret, "{t}.{corpo}") em hexadecimal.
    /// Recusa eventos com mais de 5 minutos (reenvio malicioso).
    /// </summary>
    public bool AssinaturaTikTokValida(string? cabecalho, string corpo, DateTimeOffset agora)
    {
        if (string.IsNullOrEmpty(opt.TikTokClientSecret) || string.IsNullOrEmpty(cabecalho)) return false;
        var partes = cabecalho.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!partes.TryGetValue("t", out var t) || !partes.TryGetValue("s", out var s) || !long.TryParse(t, out var ts)) return false;
        if (Math.Abs(agora.ToUnixTimeSeconds() - ts) > 300) return false;
        var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(opt.TikTokClientSecret), Encoding.UTF8.GetBytes($"{t}.{corpo}"));
        byte[] recebido;
        try { recebido = Convert.FromHexString(s); } catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(esperado, recebido);
    }

    public async Task<int> EventoTikTokAsync(string corpo, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(corpo);
        var evento = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
        var openId = doc.RootElement.TryGetProperty("user_openid", out var u) ? u.GetString() : null;
        if (evento != "authorization.removed" || string.IsNullOrEmpty(openId)) return 0;
        return await MarcarRevogadasAsync(c => c.Plataforma == PlataformaMarketing.TikTok && c.UsuarioExternoId == openId, ct);
    }

    private async Task<int> MarcarRevogadasAsync(System.Linq.Expressions.Expression<Func<ConexaoMarketing, bool>> filtro, CancellationToken ct)
    {
        tenant.UsarSistema();
        var conexoes = await db.ConexoesMarketing.Where(filtro).ToListAsync(ct);
        foreach (var c in conexoes)
        {
            c.TokenAcessoProtegido = "";
            c.TokenRefreshProtegido = null;
            c.Status = StatusIntegracao.PermissaoRevogada;
            c.UltimoErro = "Acesso removido pelo titular na plataforma.";
            c.AtualizadoEm = DateTime.UtcNow;
        }
        var ids = conexoes.Select(c => c.Id).ToList();
        foreach (var conta in await db.ContasMarketing.Where(c => ids.Contains(c.ConexaoMarketingId)).ToListAsync(ct))
        {
            conta.TokenRecursoProtegido = null;
            conta.Ativa = false;
            conta.ProximaSincronizacao = null;
            conta.Status = StatusIntegracao.PermissaoRevogada;
        }
        await db.SaveChangesAsync(ct);
        return conexoes.Count;
    }
}
