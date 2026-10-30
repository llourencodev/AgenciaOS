using Microsoft.AspNetCore.Identity;

namespace AgenciaOS.Services.Contas;

/// <summary>Mensagens do Identity em português (aparecem nas telas de cadastro, convite e senha).</summary>
public class IdentityErrosPortugues : IdentityErrorDescriber
{
    private static IdentityError E(string code, string msg) => new() { Code = code, Description = msg };

    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), $"A senha deve ter pelo menos {length} caracteres.");
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), "A senha deve ter pelo menos um número.");
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), "A senha deve ter pelo menos uma letra minúscula.");
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), "A senha deve ter pelo menos uma letra maiúscula.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric), "A senha deve ter pelo menos um símbolo.");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => E(nameof(PasswordRequiresUniqueChars), $"A senha deve ter pelo menos {uniqueChars} caracteres diferentes.");
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), "Senha atual incorreta.");
    public override IdentityError InvalidToken() => E(nameof(InvalidToken), "Link inválido ou expirado. Solicite um novo.");
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), "Este e-mail já está em uso.");
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), "Este e-mail já está em uso.");
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), "E-mail inválido.");
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), "E-mail inválido para login.");
    public override IdentityError DefaultError() => E(nameof(DefaultError), "Não foi possível concluir a operação.");
}
