using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Contas;
using AgenciaOS.Services.Plataforma;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>
/// Canal do titular de dados (público) e atendimento, pela agência, dos pedidos encaminhados a ela
/// (a agência é a controladora dos dados que inseriu; o AgenciaOS atua como operador).
/// </summary>
public class PrivacidadeController(ApplicationDbContext db, PrivacidadeService privacidade, AuditoriaService auditoria,
    UserManager<Usuario> userManager, TenantContext tenant) : Controller
{
    [AllowAnonymous]
    [HttpGet]
    public IActionResult Solicitar() => View();

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitingContas.AcoesConta)]
    public async Task<IActionResult> Solicitar(string nome, string email, TipoSolicitacaoPrivacidade tipo, string descricao, string? empresa, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(empresa)) return RedirectToAction(nameof(Recebida)); // honeypot
        if (string.IsNullOrWhiteSpace(nome) || string.IsNullOrWhiteSpace(email) || !email.Contains('@') || string.IsNullOrWhiteSpace(descricao))
        {
            ModelState.AddModelError("", "Preencha nome, e-mail e a descrição do pedido.");
            return View();
        }
        var s = await privacidade.CriarSolicitacaoAsync(nome, email, tipo, descricao, null, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        TempData["Protocolo"] = s.Protocolo;
        return RedirectToAction(nameof(Recebida));
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Recebida() => View();

    // ── Pedidos encaminhados à agência (controladora) ───────────────────
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var id = tenant.AgenciaId;
        var lista = await db.SolicitacoesPrivacidade.Where(s => id != null && s.AgenciaId == id)
            .OrderBy(s => s.Status == StatusSolicitacaoPrivacidade.Concluida || s.Status == StatusSolicitacaoPrivacidade.Recusada)
            .ThenBy(s => s.PrazoEm).ToListAsync(ct);
        return View(lista);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Responder(int id, StatusSolicitacaoPrivacidade status, string? resposta, CancellationToken ct)
    {
        var agencia = tenant.AgenciaId;
        var s = await db.SolicitacoesPrivacidade.FirstOrDefaultAsync(x => x.Id == id && agencia != null && x.AgenciaId == agencia, ct);
        if (s == null) return NotFound();
        if (status == StatusSolicitacaoPrivacidade.EncaminhadaAgencia) status = StatusSolicitacaoPrivacidade.EmAnalise;
        await privacidade.ResponderAsync(s, status, resposta, userManager.GetUserId(User)!, ct);
        await auditoria.RegistrarAsync("privacidade.responder", agencia, "SolicitacaoPrivacidade", s.Protocolo, new { status = status.ToString() }, ct);
        TempData["Sucesso"] = $"Solicitação {s.Protocolo} atualizada.";
        return RedirectToAction(nameof(Index));
    }
}
