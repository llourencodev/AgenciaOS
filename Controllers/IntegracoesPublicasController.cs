using System.Text;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>
/// Endpoints chamados pelas plataformas (sem usuário logado). Todos validam assinatura antes de agir.
/// URLs a cadastrar nos painéis de desenvolvedor: ver docs/integracoes/APROVACAO_PLATAFORMAS.md.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class IntegracoesPublicasController(ExclusaoDadosPlataformasService exclusao, ApplicationDbContext db,
    MarketingOptions opt, ILogger<IntegracoesPublicasController> logger) : Controller
{
    private string Base() => (opt.AppBaseUrl ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');

    /// <summary>Meta: "Data Deletion Request Callback URL" (Facebook e Instagram).</summary>
    [HttpPost("/integracoes/meta/exclusao-dados")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> ExclusaoMeta([FromForm(Name = "signed_request")] string? signedRequest, CancellationToken ct)
    {
        var r = await exclusao.ExclusaoMetaAsync(signedRequest, ct);
        if (r is not { } ok)
        {
            logger.LogWarning("Pedido de exclusão Meta com assinatura inválida");
            return BadRequest(new { error = "invalid signed_request" });
        }
        // Formato exigido pela Meta
        return Json(new { url = $"{Base()}/integracoes/exclusao/{ok.Codigo}", confirmation_code = ok.Codigo });
    }

    /// <summary>Meta: "Deauthorize Callback URL".</summary>
    [HttpPost("/integracoes/meta/desautorizar")]
    [RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> DesautorizarMeta([FromForm(Name = "signed_request")] string? signedRequest, CancellationToken ct)
    {
        var n = await exclusao.DesautorizacaoMetaAsync(signedRequest, ct);
        return n == null ? BadRequest() : Ok();
    }

    /// <summary>Página pública com a situação de um pedido de exclusão (link devolvido à Meta). Sem dados pessoais.</summary>
    [HttpGet("/integracoes/exclusao/{codigo}")]
    public async Task<IActionResult> SituacaoExclusao(string codigo, CancellationToken ct)
    {
        var s = await db.SolicitacoesPrivacidade.AsNoTracking()
            .Where(x => x.Protocolo == codigo && x.Protocolo.StartsWith("META-"))
            .Select(x => new { x.Protocolo, x.Status, x.ConcluidaEm }).FirstOrDefaultAsync(ct);
        if (s == null) return NotFound();
        ViewData["Protocolo"] = s.Protocolo;
        ViewData["Concluida"] = s.Status == StatusSolicitacaoPrivacidade.Concluida;
        ViewData["ConcluidaEm"] = s.ConcluidaEm;
        return View();
    }

    /// <summary>TikTok: webhook (evento authorization.removed).</summary>
    [HttpPost("/integracoes/tiktok/webhook")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> WebhookTikTok(CancellationToken ct)
    {
        using var leitor = new StreamReader(Request.Body, Encoding.UTF8);
        var corpo = await leitor.ReadToEndAsync(ct);
        if (!exclusao.AssinaturaTikTokValida(Request.Headers["TikTok-Signature"], corpo, DateTimeOffset.UtcNow))
        {
            logger.LogWarning("Webhook TikTok com assinatura inválida");
            return Unauthorized();
        }
        try { await exclusao.EventoTikTokAsync(corpo, ct); }
        catch (System.Text.Json.JsonException) { return BadRequest(); }
        return Ok();
    }
}
