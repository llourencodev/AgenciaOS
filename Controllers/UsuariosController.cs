using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Contas;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize(Roles = "Admin")]
public class UsuariosController(UserManager<Usuario> userManager, ITenantContext tenant, ApplicationDbContext db, ContasService contas,
    LimitesPlanoService limites, Services.Plataforma.AuditoriaService auditoria) : Controller
{
    static readonly string[] TodasAbas = ["Dashboard", "Clientes", "Calendario", "Arquivos", "Tarefas", "Financeiro", "Contratos", "DatasComemorativas", "Marketing"];

    /// <summary>Usuário da agência atual (Usuario não tem filtro global; aqui o filtro é explícito).</summary>
    private Task<Usuario?> BuscarDaAgencia(string id) =>
        userManager.Users.DaAgencia(tenant).FirstOrDefaultAsync(u => u.Id == id);

    public async Task<IActionResult> Index()
    {
        var usuarios = await userManager.Users.DaAgencia(tenant).OrderBy(u => u.Nome).ToListAsync();
        var lista = new List<(Usuario u, IList<string> roles)>();
        foreach (var u in usuarios)
            lista.Add((u, await userManager.GetRolesAsync(u)));

        ViewData["Lista"] = lista;
        ViewData["Convites"] = await db.ConvitesUsuario
            .Where(c => c.AceitoEm == null && c.RevogadoEm == null && c.ExpiraEm > DateTime.UtcNow)
            .OrderByDescending(c => c.CriadoEm).ToListAsync();
        return View();
    }

    public IActionResult Create() => RedirectToAction(nameof(Index));

    /// <summary>Convite por e-mail: o colaborador define a própria senha (nenhuma senha trafega pelo admin).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> Convidar(string email, string? nome, TipoUsuario tipo, string[] abasPermitidas, CancellationToken ct)
    {
        if (tipo != TipoUsuario.Cliente && await limites.VerificarUsuarioAsync(ct) is { } limite)
        {
            TempData["Erro"] = limite;
            return RedirectToAction(nameof(Index));
        }
        var admin = await userManager.GetUserAsync(User);
        var abas = abasPermitidas.Length > 0 ? string.Join(",", abasPermitidas.Where(a => TodasAbas.Contains(a))) : null;
        var (ok, erro) = await contas.ConvidarAsync(email ?? "", nome, tipo, abas, admin!, Request, ct);
        if (ok)
        {
            TempData["Sucesso"] = $"Convite enviado para {email}. O link vale 7 dias.";
            await auditoria.RegistrarAsync("usuarios.convidar", tenant.AgenciaId, "Convite", email, new { tipo = tipo.ToString(), abas }, ct);
        }
        else TempData["Erro"] = erro;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevogarConvite(int id)
    {
        var convite = await db.ConvitesUsuario.FindAsync(id); // filtro global: só convites da agência atual
        if (convite == null) return NotFound();
        convite.RevogadoEm ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        await auditoria.RegistrarAsync("usuarios.revogar-convite", tenant.AgenciaId, "Convite", convite.Email);
        TempData["Sucesso"] = "Convite revogado.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string nome, string sobrenome, string email, string senha, TipoUsuario tipo, string[] abasPermitidas)
    {
        if (tipo != TipoUsuario.Cliente && await limites.VerificarUsuarioAsync() is { } limite)
        {
            TempData["Erro"] = limite;
            return RedirectToAction(nameof(Index));
        }
        var usuario = new Usuario
        {
            UserName = email,
            Email = email,
            Nome = nome,
            Sobrenome = sobrenome,
            Tipo = tipo,
            EmailConfirmed = true,
            Ativo = true,
            AgenciaId = tenant.AgenciaId,
            AbasPermitidas = abasPermitidas.Length > 0 ? string.Join(",", abasPermitidas.Where(a => TodasAbas.Contains(a))) : null
        };

        var result = await userManager.CreateAsync(usuario, senha);
        if (!result.Succeeded)
        {
            // Não existe Views/Usuarios/Create (o formulário é um modal do Index)
            TempData["Erro"] = string.Join("; ", result.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(Index));
        }

        // Nunca SuperAdmin: esse perfil só existe por provisionamento no servidor
        var role = tipo switch
        {
            TipoUsuario.Admin              => "Admin",
            TipoUsuario.Cliente            => "Cliente",
            TipoUsuario.ColaboradorExterno => "ColaboradorExterno",
            _                              => "Equipe"
        };
        await userManager.AddToRoleAsync(usuario, role);
        await auditoria.RegistrarAsync("usuarios.criar", tenant.AgenciaId, "Usuario", usuario.Id, new { usuario.Email, perfil = role, usuario.AbasPermitidas });

        TempData["Sucesso"] = "Usuário criado com sucesso!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ativar(string id)
    {
        var u = await BuscarDaAgencia(id);
        if (u == null) return NotFound();
        if (u.Id == userManager.GetUserId(User)) return BadRequest("Você não pode desativar o próprio acesso.");
        u.Ativo = !u.Ativo;
        await userManager.UpdateAsync(u);
        // Encerra sessões abertas do usuário desativado
        if (!u.Ativo) await userManager.UpdateSecurityStampAsync(u);
        await auditoria.RegistrarAsync(u.Ativo ? "usuarios.ativar" : "usuarios.desativar", tenant.AgenciaId, "Usuario", u.Id, new { u.Email });
        return Ok(new { ativo = u.Ativo });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlterarAbas(string id, string[] abasPermitidas)
    {
        var u = await BuscarDaAgencia(id);
        if (u == null) return NotFound();
        u.AbasPermitidas = abasPermitidas.Length > 0 ? string.Join(",", abasPermitidas.Where(a => TodasAbas.Contains(a))) : null;
        await userManager.UpdateAsync(u);
        await auditoria.RegistrarAsync("usuarios.alterar-permissoes", tenant.AgenciaId, "Usuario", u.Id, new { u.Email, u.AbasPermitidas });
        TempData["Sucesso"] = "Permissões atualizadas!";
        return RedirectToAction(nameof(Index));
    }
}
