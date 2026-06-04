using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize(Roles = "Admin")]
public class UsuariosController(UserManager<Usuario> userManager, RoleManager<IdentityRole> roleManager) : Controller
{
    static readonly string[] TodasAbas = ["Dashboard", "Clientes", "Calendario", "Arquivos", "Tarefas", "Financeiro", "Contratos", "DatasComemorativas"];

    public async Task<IActionResult> Index()
    {
        var usuarios = await userManager.Users.OrderBy(u => u.Nome).ToListAsync();
        var lista = new List<(Usuario u, IList<string> roles)>();
        foreach (var u in usuarios)
            lista.Add((u, await userManager.GetRolesAsync(u)));

        ViewData["Lista"] = lista;
        return View();
    }

    public IActionResult Create()
    {
        ViewData["TodasAbas"] = TodasAbas;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string nome, string sobrenome, string email, string senha, TipoUsuario tipo, string[] abasPermitidas)
    {
        var usuario = new Usuario
        {
            UserName = email,
            Email = email,
            Nome = nome,
            Sobrenome = sobrenome,
            Tipo = tipo,
            EmailConfirmed = true,
            Ativo = true,
            AbasPermitidas = abasPermitidas.Length > 0 ? string.Join(",", abasPermitidas) : null
        };

        var result = await userManager.CreateAsync(usuario, senha);
        if (!result.Succeeded)
        {
            TempData["Erro"] = string.Join("; ", result.Errors.Select(e => e.Description));
            ViewData["TodasAbas"] = TodasAbas;
            return View();
        }

        var role = tipo switch
        {
            TipoUsuario.Admin              => "Admin",
            TipoUsuario.Cliente            => "Cliente",
            TipoUsuario.ColaboradorExterno => "ColaboradorExterno",
            _                              => "Equipe"
        };
        await userManager.AddToRoleAsync(usuario, role);

        TempData["Sucesso"] = "Usuário criado com sucesso!";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Ativar(string id)
    {
        var u = await userManager.FindByIdAsync(id);
        if (u == null) return NotFound();
        u.Ativo = !u.Ativo;
        await userManager.UpdateAsync(u);
        return Ok(new { ativo = u.Ativo });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlterarAbas(string id, string[] abasPermitidas)
    {
        var u = await userManager.FindByIdAsync(id);
        if (u == null) return NotFound();
        u.AbasPermitidas = abasPermitidas.Length > 0 ? string.Join(",", abasPermitidas) : null;
        await userManager.UpdateAsync(u);
        TempData["Sucesso"] = "Permissões atualizadas!";
        return RedirectToAction(nameof(Index));
    }
}
