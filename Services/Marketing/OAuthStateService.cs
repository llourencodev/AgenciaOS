using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenciaOS.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Gera e valida o parâmetro <c>state</c> do OAuth (proteção contra CSRF).
/// O estado fica num cookie HttpOnly criptografado e amarrado ao usuário logado,
/// ao cliente e à plataforma. Expira em 10 minutos e só pode ser usado uma vez.
/// </summary>
public class OAuthStateService(IDataProtectionProvider provider, TimeProvider? relogio = null)
{
    public const string NomeCookie = "aos_mkt_oauth";
    private static readonly TimeSpan Validade = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = provider.CreateProtector("AgenciaOS.Marketing.OAuthState.v1");
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public record EstadoOAuth(string State, string UsuarioId, int ClienteId, PlataformaMarketing Plataforma, string CodeVerifier, DateTimeOffset ExpiraEm);

    public record EstadoGerado(EstadoOAuth Estado, string CookieProtegido)
    {
        /// <summary>code_challenge PKCE (S256) derivado do verifier.</summary>
        public string CodeChallenge => GerarChallenge(Estado.CodeVerifier);
    }

    public EstadoGerado Gerar(string usuarioId, int clienteId, PlataformaMarketing plataforma)
    {
        var estado = new EstadoOAuth(
            Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            usuarioId,
            clienteId,
            plataforma,
            Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(48)),
            _relogio.GetUtcNow().Add(Validade));

        return new EstadoGerado(estado, _protector.Protect(JsonSerializer.Serialize(estado)));
    }

    /// <summary>
    /// Valida o state recebido no callback. Retorna null (e o motivo) se for inválido.
    /// </summary>
    public EstadoOAuth? Validar(string? cookieProtegido, string? stateRecebido, string usuarioId, PlataformaMarketing plataforma, out string motivo)
    {
        motivo = "";
        if (string.IsNullOrEmpty(cookieProtegido) || string.IsNullOrEmpty(stateRecebido))
        {
            motivo = "Sessão de autorização não encontrada. Inicie a conexão novamente.";
            return null;
        }

        EstadoOAuth? estado;
        try
        {
            estado = JsonSerializer.Deserialize<EstadoOAuth>(_protector.Unprotect(cookieProtegido));
        }
        catch
        {
            motivo = "Sessão de autorização inválida.";
            return null;
        }

        if (estado == null ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(estado.State), Encoding.UTF8.GetBytes(stateRecebido)))
        {
            motivo = "O parâmetro de segurança (state) não confere. A autorização foi bloqueada.";
            return null;
        }
        if (estado.ExpiraEm < _relogio.GetUtcNow())
        {
            motivo = "A autorização expirou. Inicie a conexão novamente.";
            return null;
        }
        if (estado.UsuarioId != usuarioId || estado.Plataforma != plataforma)
        {
            motivo = "A autorização não pertence a este usuário ou plataforma.";
            return null;
        }
        return estado;
    }

    public static CookieOptions OpcoesCookie(bool https) => new()
    {
        HttpOnly = true,
        Secure = https,
        SameSite = SameSiteMode.Lax, // precisa voltar no redirecionamento top-level do provedor
        Path = "/Integracoes",
        MaxAge = Validade,
        IsEssential = true
    };

    public static string GerarChallenge(string verifier) =>
        Base64UrlTextEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
