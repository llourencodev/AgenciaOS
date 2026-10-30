using AgenciaOS.Models;
using AgenciaOS.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgenciaOS.Controllers;

/// <summary>Segurança da própria conta: troca de senha e encerramento de sessões. Vale para qualquer perfil.</summary>
[Authorize]
public class ContaController(UserManager<Usuario> userManager, SignInManager<Usuario> signInManager, IEnviadorEmail email) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Seguranca()
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        if (User.IsInRole("SuperAdmin") && u.AgenciaId == null) ViewData["Layout"] = "~/Views/Plataforma/_LayoutPlataforma.cshtml";
        return View(u);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlterarSenha(string senhaAtual, string novaSenha, string confirmarSenha, CancellationToken ct)
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        if (novaSenha != confirmarSenha)
        {
            TempData["Erro"] = "As senhas não conferem.";
            return RedirectToAction(nameof(Seguranca));
        }

        // ChangePasswordAsync troca o security stamp: as outras sessões caem; esta é renovada abaixo
        var r = await userManager.ChangePasswordAsync(u, senhaAtual ?? "", novaSenha ?? "");
        if (!r.Succeeded)
        {
            TempData["Erro"] = string.Join(" ", r.Errors.Select(e => e.Description));
            return RedirectToAction(nameof(Seguranca));
        }
        await signInManager.RefreshSignInAsync(u);
        try { await email.EnviarAsync(TemplatesEmail.SenhaAlterada(u.Email!, u.Nome), ct); }
        catch { /* o aviso por e-mail não pode impedir a troca de senha */ }
        TempData["Sucesso"] = "Senha alterada. As outras sessões foram encerradas.";
        return RedirectToAction(nameof(Seguranca));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EncerrarOutrasSessoes()
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        await userManager.UpdateSecurityStampAsync(u);
        await signInManager.RefreshSignInAsync(u);
        TempData["Sucesso"] = "As sessões em outros dispositivos foram encerradas.";
        return RedirectToAction(nameof(Seguranca));
    }
}
