using System.Security.Cryptography;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Data;

/// <summary>
/// Executado no startup em modo Sistema. Não usa senhas padrão:
/// - ADMIN_EMAIL / ADMIN_PASSWORD: primeiro admin da agência inicial (só em banco sem usuários).
/// - SUPERADMIN_EMAIL / SUPERADMIN_PASSWORD: superadministrador da plataforma (sem agência).
/// </summary>
public static class SeedInicial
{
    public static readonly string[] Roles = ["SuperAdmin", "Admin", "Equipe", "Cliente", "ColaboradorExterno"];

    public static async Task ExecutarAsync(IServiceProvider sp, IHostEnvironment env, ILogger logger)
    {
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var userMgr = sp.GetRequiredService<UserManager<Usuario>>();
        var roleMgr = sp.GetRequiredService<RoleManager<IdentityRole>>();

        await db.Database.MigrateAsync();

        foreach (var role in Roles)
            if (!await roleMgr.RoleExistsAsync(role))
                await roleMgr.CreateAsync(new IdentityRole(role));

        await CriarPrimeiroAdminAsync(db, userMgr, env, logger);
        await CriarSuperAdminAsync(userMgr, logger);
    }

    private static async Task CriarPrimeiroAdminAsync(ApplicationDbContext db, UserManager<Usuario> userMgr, IHostEnvironment env, ILogger logger)
    {
        if (await db.Users.AnyAsync()) return;

        // A migration MultiTenancyAgencias cria a agência inicial (Id 1)
        var agenciaId = await db.Agencias.OrderBy(a => a.Id).Select(a => (int?)a.Id).FirstOrDefaultAsync();
        if (agenciaId == null) return;

        var email = Environment.GetEnvironmentVariable("ADMIN_EMAIL") ?? "admin@agencia.com";
        var senha = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(senha))
        {
            if (!env.IsDevelopment())
            {
                logger.LogWarning("Banco sem usuários e ADMIN_PASSWORD não definida: o primeiro administrador não foi criado.");
                return;
            }
            senha = GerarSenha();
            logger.LogWarning("Administrador de desenvolvimento criado: {Email} / {Senha} (troque após o primeiro acesso)", email, senha);
        }

        var admin = new Usuario
        {
            UserName = email, Email = email, Nome = "Administrador",
            Tipo = TipoUsuario.Admin, EmailConfirmed = true, AgenciaId = agenciaId
        };
        var r = await userMgr.CreateAsync(admin, senha);
        if (r.Succeeded) await userMgr.AddToRoleAsync(admin, "Admin");
        else logger.LogError("Falha ao criar administrador inicial: {Erros}", string.Join("; ", r.Errors.Select(e => e.Description)));
    }

    /// <summary>Único caminho para existir um superadmin. Não há tela nem endpoint que promova usuários.</summary>
    private static async Task CriarSuperAdminAsync(UserManager<Usuario> userMgr, ILogger logger)
    {
        var email = Environment.GetEnvironmentVariable("SUPERADMIN_EMAIL");
        var senha = Environment.GetEnvironmentVariable("SUPERADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email)) return;

        var existente = await userMgr.FindByEmailAsync(email);
        if (existente != null)
        {
            if (existente.AgenciaId != null)
                logger.LogError("SUPERADMIN_EMAIL {Email} pertence a uma agência; superadmin não foi provisionado.", email);
            else if (!await userMgr.IsInRoleAsync(existente, "SuperAdmin"))
                await userMgr.AddToRoleAsync(existente, "SuperAdmin");
            return;
        }

        if (string.IsNullOrWhiteSpace(senha) || senha.Length < 12)
        {
            logger.LogError("SUPERADMIN_PASSWORD ausente ou com menos de 12 caracteres; superadmin não foi provisionado.");
            return;
        }

        var su = new Usuario
        {
            UserName = email, Email = email, Nome = "Plataforma",
            Tipo = TipoUsuario.Admin, EmailConfirmed = true, AgenciaId = null
        };
        var r = await userMgr.CreateAsync(su, senha);
        if (r.Succeeded)
        {
            await userMgr.AddToRoleAsync(su, "SuperAdmin");
            logger.LogInformation("Superadministrador provisionado: {Email}", email);
        }
        else logger.LogError("Falha ao criar superadmin: {Erros}", string.Join("; ", r.Errors.Select(e => e.Description)));
    }

    public static string GerarSenha() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', 'x').Replace('/', 'y') + "Aa9!";
}
