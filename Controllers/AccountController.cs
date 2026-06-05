using AgenciaOS.Models;
using AgenciaOS.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgenciaOS.Controllers;

public class AccountController(SignInManager<Usuario> signInManager, UserManager<Usuario> userManager) : Controller
{
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");

        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await signInManager.PasswordSignInAsync(model.Email, model.Senha, model.LembrarMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            var user = await userManager.FindByEmailAsync(model.Email);
            if (user != null && !user.Ativo)
            {
                await signInManager.SignOutAsync();
                ModelState.AddModelError("", "Sua conta foi desativada. Entre em contato com o administrador.");
                return View(model);
            }
            if (user != null)
            {
                user.UltimoAcesso = DateTime.UtcNow;
                await userManager.UpdateAsync(user);
            }
            return LocalRedirect(returnUrl ?? "/");
        }

        if (result.IsLockedOut)
        {
            ModelState.AddModelError("", "Conta bloqueada por excesso de tentativas. Tente novamente em 5 minutos.");
            return View(model);
        }

        ModelState.AddModelError("", "E-mail ou senha inválidos.");
        return View(model);
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction("Login");
    }

    [HttpGet]
    public IActionResult AcessoNegado() => View();
}
