using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Contas;
using AgenciaOS.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

public class AccountController(
    SignInManager<Usuario> signInManager,
    UserManager<Usuario> userManager,
    ApplicationDbContext db,
    ContasService contas,
    Services.Plataforma.AuditoriaService auditoria,
    ILogger<AccountController> logger) : Controller
{
    // ── Login ────────────────────────────────────────────────────────────
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
    [EnableRateLimiting(RateLimitingContas.Login)]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await userManager.FindByEmailAsync(model.Email);
        if (user != null && (!user.Ativo || !await AgenciaAtivaAsync(user)))
        {
            ModelState.AddModelError("", "Sua conta está desativada. Entre em contato com o administrador.");
            return View(model);
        }

        var result = await signInManager.PasswordSignInAsync(model.Email, model.Senha, model.LembrarMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            if (user != null)
            {
                user.UltimoAcesso = DateTime.UtcNow;
                await userManager.UpdateAsync(user);
                await auditoria.RegistrarAsync("conta.login", user.AgenciaId, "Usuario", user.Id);
            }
            if (user?.AgenciaId == null && user != null && await userManager.IsInRoleAsync(user, "SuperAdmin"))
                return RedirectToAction("Index", "Plataforma");
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
        }

        if (result.RequiresTwoFactor)
            return RedirectToAction(nameof(LoginDoisFatores), new { returnUrl, lembrarMe = model.LembrarMe });

        if (result.IsLockedOut)
        {
            await auditoria.RegistrarAsync("conta.login-bloqueado", user?.AgenciaId, "Usuario", user?.Id, new { email = model.Email });
            ModelState.AddModelError("", "Conta bloqueada por excesso de tentativas. Tente novamente em 5 minutos.");
            return View(model);
        }

        if (result.IsNotAllowed)
        {
            // Senha correta, e-mail não confirmado
            ViewData["EmailNaoConfirmado"] = model.Email;
            ModelState.AddModelError("", "Confirme seu e-mail para entrar. Verifique sua caixa de entrada.");
            return View(model);
        }

        await auditoria.RegistrarAsync("conta.login-falhou", user?.AgenciaId, "Usuario", user?.Id, new { email = model.Email });
        ModelState.AddModelError("", "E-mail ou senha inválidos.");
        return View(model);
    }

    // ── Segunda etapa (TOTP ou código de recuperação) ────────────────────
    [HttpGet]
    public async Task<IActionResult> LoginDoisFatores(string? returnUrl, bool lembrarMe)
    {
        // Só existe depois de senha correta (cookie temporário do Identity)
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() == null) return RedirectToAction(nameof(Login));
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["LembrarMe"] = lembrarMe;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.Login)]
    public async Task<IActionResult> LoginDoisFatores(string codigo, bool usarRecuperacao, bool lembrarDispositivo, bool lembrarMe, string? returnUrl)
    {
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user == null) return RedirectToAction(nameof(Login));
        ViewData["ReturnUrl"] = returnUrl;
        ViewData["LembrarMe"] = lembrarMe;

        var result = usarRecuperacao
            ? await signInManager.TwoFactorRecoveryCodeSignInAsync(CodigosDoisFatores.Recuperacao(codigo))
            : await signInManager.TwoFactorAuthenticatorSignInAsync(CodigosDoisFatores.Totp(codigo), lembrarMe, lembrarDispositivo);

        if (result.Succeeded)
        {
            user.UltimoAcesso = DateTime.UtcNow;
            await userManager.UpdateAsync(user);
            await auditoria.RegistrarAsync(usarRecuperacao ? "conta.login-2fa-recuperacao" : "conta.login-2fa", user.AgenciaId, "Usuario", user.Id);
            if (user.AgenciaId == null && await userManager.IsInRoleAsync(user, "SuperAdmin"))
                return RedirectToAction("Index", "Plataforma");
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
        }

        await auditoria.RegistrarAsync(result.IsLockedOut ? "conta.login-2fa-bloqueado" : "conta.login-2fa-falhou", user.AgenciaId, "Usuario", user.Id);
        ModelState.AddModelError("", result.IsLockedOut
            ? "Conta bloqueada por excesso de tentativas. Tente novamente em 5 minutos."
            : "Código inválido.");
        return View();
    }

    private async Task<bool> AgenciaAtivaAsync(Usuario user) =>
        user.AgenciaId == null || await db.Agencias.AnyAsync(a => a.Id == user.AgenciaId && a.Status == StatusAgencia.Ativa);

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

    // ── Cadastro público de agência ──────────────────────────────────────
    [HttpGet]
    public IActionResult Cadastro()
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Dashboard");
        return View(new CadastroAgenciaInput());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> Cadastro(CadastroAgenciaInput input, CancellationToken ct)
    {
        // Robô preencheu o campo invisível: resposta igual à de sucesso, nada é criado
        if (!string.IsNullOrEmpty(input.Website))
        {
            logger.LogWarning("Cadastro descartado pelo honeypot ({Ip})", HttpContext.Connection.RemoteIpAddress);
            return RedirectToAction(nameof(VerifiqueEmail));
        }
        if (!input.AceitaTermos)
            ModelState.AddModelError(nameof(input.AceitaTermos), "É preciso aceitar os Termos de Uso e a Política de Privacidade.");
        if (!ModelState.IsValid) return View(input);

        var r = await contas.CadastrarAgenciaAsync(input, Request, ct);
        if (!r.Ok)
        {
            foreach (var e in r.Erros) ModelState.AddModelError(nameof(input.Senha), e);
            return View(input);
        }
        return RedirectToAction(nameof(VerifiqueEmail));
    }

    [HttpGet]
    public IActionResult VerifiqueEmail() => View();

    [HttpGet]
    public async Task<IActionResult> ConfirmarEmail(string? userId, string? code)
    {
        var token = ContasService.DecodificarToken(code);
        var user = string.IsNullOrEmpty(userId) ? null : await userManager.FindByIdAsync(userId);
        var ok = user != null && token != null && (await userManager.ConfirmEmailAsync(user, token)).Succeeded;
        ViewData["Confirmado"] = ok;
        return View();
    }

    [HttpGet]
    public IActionResult ReenviarConfirmacao(string? email) => View(model: email);

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> ReenviarConfirmacao(string email, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email.Trim());
        if (user is { EmailConfirmed: false, Ativo: true })
        {
            var agencia = await db.Agencias.Where(a => a.Id == user.AgenciaId).Select(a => a.Nome).FirstOrDefaultAsync(ct) ?? "AgenciaOS";
            await contas.EnviarConfirmacaoAsync(user, agencia, Request, ct);
        }
        // Mesma resposta exista ou não a conta (anti-enumeração)
        return RedirectToAction(nameof(VerifiqueEmail));
    }

    // ── Recuperação de senha ─────────────────────────────────────────────
    [HttpGet]
    public IActionResult EsqueciSenha() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> EsqueciSenha(string email, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email.Trim());
        if (user is { Ativo: true })
            await contas.EnviarRecuperacaoAsync(user, Request, ct);
        TempData["EmailEnviado"] = true;
        return RedirectToAction(nameof(EsqueciSenha));
    }

    [HttpGet]
    public IActionResult RedefinirSenha(string? email, string? code)
    {
        ViewData["Email"] = email;
        ViewData["Code"] = code;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.Login)]
    public async Task<IActionResult> RedefinirSenha(string email, string code, string senha, string confirmarSenha)
    {
        ViewData["Email"] = email;
        ViewData["Code"] = code;
        if (senha != confirmarSenha)
        {
            ModelState.AddModelError("", "As senhas não conferem.");
            return View();
        }

        var user = string.IsNullOrWhiteSpace(email) ? null : await userManager.FindByEmailAsync(email.Trim());
        var token = ContasService.DecodificarToken(code);
        if (user == null || token == null)
        {
            ModelState.AddModelError("", "Link inválido ou expirado. Solicite um novo.");
            return View();
        }

        // ResetPasswordAsync troca o security stamp: todas as sessões abertas caem
        var r = await userManager.ResetPasswordAsync(user, token, senha);
        if (!r.Succeeded)
        {
            foreach (var e in r.Errors) ModelState.AddModelError("", e.Description);
            return View();
        }
        // Quem recebeu o link no e-mail provou ser dono dele
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await userManager.UpdateAsync(user);
        }
        await userManager.ResetAccessFailedCountAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        TempData["Sucesso"] = "Senha redefinida. Entre com a nova senha.";
        return RedirectToAction(nameof(Login));
    }

    // ── Convites ─────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Convite(string? token, CancellationToken ct)
    {
        var (convite, agencia) = await contas.BuscarConviteAsync(token, ct);
        ViewData["Token"] = token;
        ViewData["Agencia"] = agencia;
        ViewData["Email"] = convite?.Email;
        ViewData["Nome"] = convite?.Nome;
        ViewData["Valido"] = convite != null;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> Convite(string token, string nome, string senha, string confirmarSenha, bool aceitaTermos, CancellationToken ct)
    {
        var erros = senha != confirmarSenha
            ? ["As senhas não conferem."]
            : await contas.AceitarConviteAsync(token, nome, senha, aceitaTermos, Request, ct);
        if (erros.Count == 0)
        {
            TempData["Sucesso"] = "Acesso criado. Entre com seu e-mail e a senha que você definiu.";
            return RedirectToAction(nameof(Login));
        }

        foreach (var e in erros) ModelState.AddModelError("", e);
        var (convite, agencia) = await contas.BuscarConviteAsync(token, ct);
        ViewData["Token"] = token;
        ViewData["Agencia"] = agencia;
        ViewData["Email"] = convite?.Email;
        ViewData["Nome"] = nome;
        ViewData["Valido"] = convite != null;
        return View();
    }

    // ── Documentos legais (minutas para revisão jurídica) ────────────────
    [HttpGet]
    public IActionResult Termos() => View();

    [HttpGet]
    public IActionResult Privacidade() => View();
}
