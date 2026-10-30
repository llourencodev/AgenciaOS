using Microsoft.AspNetCore.DataProtection;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Criptografa tokens OAuth em repouso usando o Data Protection da aplicação
/// (chaves já persistidas na tabela DataProtectionKeys).
/// </summary>
public class ProtetorTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("AgenciaOS.Marketing.OAuthTokens.v1");

    public string Proteger(string token) => _protector.Protect(token);

    public string? ProtegerOpcional(string? token) => string.IsNullOrEmpty(token) ? null : _protector.Protect(token);

    public string Revelar(string tokenProtegido) => _protector.Unprotect(tokenProtegido);

    public string? RevelarOpcional(string? tokenProtegido) =>
        string.IsNullOrEmpty(tokenProtegido) ? null : _protector.Unprotect(tokenProtegido);
}
