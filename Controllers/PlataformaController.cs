using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>
/// Painel do proprietário da plataforma (Fase 1: mínimo para operar várias agências).
/// Separado da navegação das agências; métricas, planos e auditoria entram na Fase 5.
/// </summary>
[Authorize(Roles = "SuperAdmin")]
public class PlataformaController(ApplicationDbContext db, UserManager<Usuario> userManager, TenantContext tenant) : Controller
{
    public class NovaAgenciaInput
    {
        [Required, StringLength(150)] public string Nome { get; set; } = "";
        [StringLength(150)] public string? NomeFantasia { get; set; }
        [StringLength(60), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Use letras minúsculas, números e hífen.")]
        public string? Slug { get; set; }
        [StringLength(18)] public string? Cnpj { get; set; }
        [EmailAddress, StringLength(256)] public string? EmailPrincipal { get; set; }
        [StringLength(30)] public string? Telefone { get; set; }
        [Required, StringLength(100)] public string AdminNome { get; set; } = "";
        [Required, EmailAddress] public string AdminEmail { get; set; } = "";
        [Required, MinLength(10)] public string AdminSenha { get; set; } = "";
    }

    public record LinhaAgencia(Agencia Agencia, int Usuarios, int Clientes);

    public async Task<IActionResult> Index(string? busca)
    {
        tenant.UsarSistema(); // leitura agregada de todas as agências (somente SuperAdmin chega aqui)

        var query = db.Agencias.AsQueryable();
        if (!string.IsNullOrWhiteSpace(busca))
            query = query.Where(a => a.Nome.Contains(busca) || (a.NomeFantasia != null && a.NomeFantasia.Contains(busca)) || a.Slug.Contains(busca));

        var agencias = await query.OrderBy(a => a.Nome).ToListAsync();
        var usuarios = await db.Users.Where(u => u.AgenciaId != null).GroupBy(u => u.AgenciaId!.Value)
            .Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total);
        var clientes = await db.Clientes.GroupBy(c => c.AgenciaId)
            .Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total);

        ViewData["Busca"] = busca;
        return View(agencias.Select(a => new LinhaAgencia(a, usuarios.GetValueOrDefault(a.Id), clientes.GetValueOrDefault(a.Id))).ToList());
    }

    public IActionResult NovaAgencia() => View(new NovaAgenciaInput());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NovaAgencia(NovaAgenciaInput input)
    {
        input.Slug = string.IsNullOrWhiteSpace(input.Slug) ? GerarSlug(input.NomeFantasia ?? input.Nome) : input.Slug.Trim();
        if (await db.Agencias.AnyAsync(a => a.Slug == input.Slug))
            ModelState.AddModelError(nameof(input.Slug), "Já existe uma agência com esse identificador.");
        if (await userManager.FindByEmailAsync(input.AdminEmail) != null)
            ModelState.AddModelError(nameof(input.AdminEmail), "Este e-mail já é usado por outro usuário da plataforma.");
        if (!ModelState.IsValid) return View(input);

        tenant.UsarSistema();
        await using var tx = await db.Database.BeginTransactionAsync();

        var agencia = new Agencia
        {
            Nome = input.Nome.Trim(), NomeFantasia = input.NomeFantasia?.Trim(), Slug = input.Slug,
            Cnpj = input.Cnpj, EmailPrincipal = input.EmailPrincipal, Telefone = input.Telefone,
            Status = StatusAgencia.Ativa, SituacaoAssinatura = SituacaoAssinatura.Teste
        };
        db.Agencias.Add(agencia);
        await db.SaveChangesAsync();

        db.Configuracoes.Add(new Configuracao { AgenciaId = agencia.Id, NomeAgencia = agencia.NomeExibicao });
        await db.SaveChangesAsync();

        var admin = new Usuario
        {
            UserName = input.AdminEmail, Email = input.AdminEmail, Nome = input.AdminNome.Trim(),
            Tipo = TipoUsuario.Admin, EmailConfirmed = true, AgenciaId = agencia.Id
        };
        var r = await userManager.CreateAsync(admin, input.AdminSenha);
        if (!r.Succeeded)
        {
            await tx.RollbackAsync();
            foreach (var e in r.Errors) ModelState.AddModelError(nameof(input.AdminSenha), e.Description);
            return View(input);
        }
        await userManager.AddToRoleAsync(admin, "Admin");
        await tx.CommitAsync();

        TempData["Sucesso"] = $"Agência {agencia.NomeExibicao} criada. Administrador: {admin.Email}.";
        return RedirectToAction(nameof(Index));
    }

    internal static string GerarSlug(string texto)
    {
        var normalizado = new string(texto.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        var slug = Regex.Replace(normalizado, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
