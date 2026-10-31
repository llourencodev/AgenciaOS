namespace AgenciaOS.Services.Contas;

/// <summary>Normaliza o que o usuário digita nos campos de 2FA.</summary>
public static class CodigosDoisFatores
{
    /// <summary>Código do aplicativo: só os dígitos ("123 456" → "123456").</summary>
    public static string Totp(string? codigo) => new((codigo ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Código de recuperação do Identity tem o formato "XXXXX-XXXXX"; aceita digitado sem o hífen ou em minúsculas.</summary>
    public static string Recuperacao(string? codigo)
    {
        var s = new string((codigo ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length == 10 ? $"{s[..5]}-{s[5..]}" : s;
    }
}
