using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Contas;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Plataforma;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgenciaOS.Controllers;

/// <summary>
/// Conta do próprio usuário: segurança (senha, sessões) e privacidade (aceite de termos, exportação dos
/// próprios dados, pedidos ao encarregado). Vale para qualquer perfil.
/// </summary>
[Authorize]
public class ContaController(UserManager<Usuario> userManager, SignInManager<Usuario> signInManager, IEnviadorEmail email,
    ContasService contas, PrivacidadeService privacidade, AuditoriaService auditoria, IMemoryCache cache, ApplicationDbContext db) : Controller
{
    // ── Privacidade ──────────────────────────────────────────────────────
    [HttpGet]
    public IActionResult AceitarTermos(string? returnUrl)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AceitarTermos(bool aceita, string? returnUrl)
    {
        if (!aceita)
        {
            ModelState.AddModelError("", "Para continuar usando o AgenciaOS é preciso aceitar os documentos atualizados.");
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        contas.RegistrarAceites(u.Id, u.AgenciaId, Request);
        await db.SaveChangesAsync();
        cache.Remove(AceiteTermosFilter.ChaveCache(u.Id));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [HttpGet]
    public async Task<IActionResult> Privacidade()
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        if (User.IsInRole("SuperAdmin") && u.AgenciaId == null) ViewData["Layout"] = "~/Views/Plataforma/_LayoutPlataforma.cshtml";
        ViewData["Aceites"] = await db.ConsentimentosTermos.Where(c => c.UsuarioId == u.Id).OrderByDescending(c => c.AceitoEm).ToListAsync();
        ViewData["Solicitacoes"] = await db.SolicitacoesPrivacidade.Where(s => s.UsuarioId == u.Id).OrderByDescending(s => s.CriadaEm).ToListAsync();
        return View(u);
    }

    /// <summary>Acesso/portabilidade: JSON com os dados pessoais do próprio usuário.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> ExportarMeusDados(CancellationToken ct)
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        var bytes = await privacidade.ExportarUsuarioAsync(u.Id, ct);
        await auditoria.RegistrarAsync("privacidade.exportar-proprios-dados", u.AgenciaId, "Usuario", u.Id, ct: ct);
        return File(bytes, "application/json", $"meus-dados-agenciaos-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> SolicitarPrivacidade(TipoSolicitacaoPrivacidade tipo, string descricao, CancellationToken ct)
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        var s = await privacidade.CriarSolicitacaoAsync(u.NomeCompleto, u.Email!, tipo, descricao ?? "", u, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        TempData["Sucesso"] = $"Solicitação registrada com o protocolo {s.Protocolo}. Prazo de resposta: {s.PrazoEm.ToLocalTime():dd/MM/yyyy}.";
        return RedirectToAction(nameof(Privacidade));
    }
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

    // ── Autenticação em duas etapas (TOTP) ───────────────────────────────
    public record ConfiguracaoDoisFatores(bool Ativo, string? Chave, string? UriAutenticador, int CodigosRestantes, string[]? NovosCodigos);

    private void UsarLayoutDoUsuario(Usuario u)
    {
        if (User.IsInRole("SuperAdmin") && u.AgenciaId == null) ViewData["Layout"] = "~/Views/Plataforma/_LayoutPlataforma.cshtml";
    }

    private async Task<ConfiguracaoDoisFatores> MontarDoisFatoresAsync(Usuario u, string[]? novosCodigos = null)
    {
        if (await userManager.GetTwoFactorEnabledAsync(u))
            return new(true, null, null, await userManager.CountRecoveryCodesAsync(u), novosCodigos);

        var chave = await userManager.GetAuthenticatorKeyAsync(u);
        if (string.IsNullOrEmpty(chave))
        {
            // Gerar a chave troca o security stamp: renova o cookie desta sessão para não derrubar o próprio usuário
            await userManager.ResetAuthenticatorKeyAsync(u);
            await signInManager.RefreshSignInAsync(u);
            chave = await userManager.GetAuthenticatorKeyAsync(u);
        }
        var uri = $"otpauth://totp/{Uri.EscapeDataString("AgenciaOS")}:{Uri.EscapeDataString(u.Email!)}?secret={chave}&issuer=AgenciaOS&digits=6";
        var formatada = string.Join(" ", Enumerable.Range(0, (chave!.Length + 3) / 4).Select(i => chave.Substring(i * 4, Math.Min(4, chave.Length - i * 4)))).ToLowerInvariant();
        return new(false, formatada, uri, 0, null);
    }

    [HttpGet]
    public async Task<IActionResult> DoisFatores()
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        UsarLayoutDoUsuario(u);
        return View(await MontarDoisFatoresAsync(u));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.Login)]
    public async Task<IActionResult> AtivarDoisFatores(string codigo, CancellationToken ct)
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        UsarLayoutDoUsuario(u);
        if (!await userManager.VerifyTwoFactorTokenAsync(u, userManager.Options.Tokens.AuthenticatorTokenProvider, CodigosDoisFatores.Totp(codigo)))
        {
            ModelState.AddModelError("", "Código inválido. Confira o horário do celular e tente o código atual.");
            return View(nameof(DoisFatores), await MontarDoisFatoresAsync(u));
        }

        await userManager.SetTwoFactorEnabledAsync(u, true);
        var codigos = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(u, 10))!.ToArray();
        await signInManager.RefreshSignInAsync(u);
        await auditoria.RegistrarAsync("conta.2fa-ativado", u.AgenciaId, "Usuario", u.Id, ct: ct);
        await AvisarAsync(u, "Autenticação em duas etapas ativada", "A autenticação em duas etapas foi ativada na sua conta.", ct);
        return View(nameof(DoisFatores), await MontarDoisFatoresAsync(u, codigos));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.Login)]
    public async Task<IActionResult> NovosCodigosRecuperacao(string codigo, CancellationToken ct)
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        UsarLayoutDoUsuario(u);
        if (!await userManager.GetTwoFactorEnabledAsync(u)
            || !await userManager.VerifyTwoFactorTokenAsync(u, userManager.Options.Tokens.AuthenticatorTokenProvider, CodigosDoisFatores.Totp(codigo)))
        {
            ModelState.AddModelError("", "Código inválido.");
            return View(nameof(DoisFatores), await MontarDoisFatoresAsync(u));
        }
        var codigos = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(u, 10))!.ToArray();
        await auditoria.RegistrarAsync("conta.2fa-novos-codigos", u.AgenciaId, "Usuario", u.Id, ct: ct);
        return View(nameof(DoisFatores), await MontarDoisFatoresAsync(u, codigos));
    }

    /// <summary>Desativar exige senha + código (TOTP ou recuperação): uma sessão roubada sozinha não basta.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.Login)]
    public async Task<IActionResult> DesativarDoisFatores(string senha, string codigo, CancellationToken ct)
    {
        var u = await userManager.GetUserAsync(User);
        if (u == null) return Challenge();
        UsarLayoutDoUsuario(u);
        var codigoOk = await userManager.VerifyTwoFactorTokenAsync(u, userManager.Options.Tokens.AuthenticatorTokenProvider, CodigosDoisFatores.Totp(codigo))
                       || (await userManager.RedeemTwoFactorRecoveryCodeAsync(u, CodigosDoisFatores.Recuperacao(codigo))).Succeeded;
        if (!await userManager.CheckPasswordAsync(u, senha ?? "") || !codigoOk)
        {
            ModelState.AddModelError("", "Senha ou código inválido.");
            return View(nameof(DoisFatores), await MontarDoisFatoresAsync(u));
        }

        await userManager.SetTwoFactorEnabledAsync(u, false);
        await userManager.ResetAuthenticatorKeyAsync(u); // chave antiga deixa de valer
        await signInManager.RefreshSignInAsync(u);
        await auditoria.RegistrarAsync("conta.2fa-desativado", u.AgenciaId, "Usuario", u.Id, ct: ct);
        await AvisarAsync(u, "Autenticação em duas etapas desativada",
            "A autenticação em duas etapas foi desativada na sua conta. Se não foi você, troque a senha imediatamente.", ct);
        TempData["Sucesso"] = "Autenticação em duas etapas desativada.";
        return RedirectToAction(nameof(DoisFatores));
    }

    private async Task AvisarAsync(Usuario u, string assunto, string texto, CancellationToken ct)
    {
        try { await email.EnviarAsync(TemplatesEmail.Montar(u.Email!, $"AgenciaOS: {assunto}", assunto, System.Net.WebUtility.HtmlEncode(texto), categoria: "seguranca"), ct); }
        catch { /* aviso por e-mail não pode impedir a operação */ }
    }
}
