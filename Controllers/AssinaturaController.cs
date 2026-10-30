using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>
/// Plano e assinatura da agência atual. Única área acessível quando a agência está bloqueada por falta de pagamento.
/// A confirmação de pagamento vem do webhook do gateway, nunca daqui.
/// </summary>
[Authorize]
public class AssinaturaController(ApplicationDbContext db, CobrancaService servico, TenantContext tenant,
    CobrancaOptions opt, IWebHostEnvironment env, Services.Plataforma.AuditoriaService auditoria) : Controller
{
    public record Painel(Agencia Agencia, Assinatura? Assinatura, List<Pagamento> Pagamentos, List<Plano> Planos, bool Bloqueada);

    private async Task<Painel> MontarAsync(CancellationToken ct)
    {
        var agenciaId = tenant.AgenciaId!.Value;
        var agencia = await db.Agencias.SingleAsync(a => a.Id == agenciaId, ct);
        var assinatura = await servico.VigenteAsync(agenciaId, ct);
        var pagamentos = await db.Pagamentos.OrderByDescending(p => p.Vencimento).ThenByDescending(p => p.Id).Take(24).ToListAsync(ct);
        var planos = await db.Planos.Where(p => p.Ativo).OrderBy(p => p.Ordem).ThenBy(p => p.Preco).ToListAsync(ct);
        var bloqueada = agencia.Status != StatusAgencia.Ativa || !agencia.Isenta && CobrancaService.Bloqueada(agencia.SituacaoAssinatura, agencia.TesteExpiraEm, DateTime.UtcNow);
        return new Painel(agencia, assinatura, pagamentos, planos, bloqueada);
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await MontarAsync(ct));

    /// <summary>Tela de bloqueio: admin vê como regularizar; demais perfis, que devem falar com o admin.</summary>
    public async Task<IActionResult> Bloqueada(CancellationToken ct)
    {
        if (User.IsInRole("Admin")) return RedirectToAction(nameof(Index));
        return View(await MontarAsync(ct));
    }

    public IActionResult ModuloIndisponivel(string? modulo)
    {
        ViewData["Modulo"] = modulo;
        return View();
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Planos(CancellationToken ct) => View(await MontarAsync(ct));

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Assinar(int planoId, FormaPagamento forma, string cpfCnpj, CancellationToken ct)
    {
        try
        {
            var url = await servico.AssinarAsync(tenant.AgenciaId!.Value, planoId, forma, cpfCnpj, ct);
            await auditoria.RegistrarAsync("assinatura.assinar", tenant.AgenciaId, "Plano", planoId.ToString(), new { forma = forma.ToString() }, ct);
            // Checkout hospedado no gateway (Asaas). Só aceita URL do próprio app ou HTTPS externa.
            return url.StartsWith('/') || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? Redirect(url) : RedirectToAction(nameof(Index));
        }
        catch (ErroGateway e)
        {
            TempData["Erro"] = e.Message;
            return RedirectToAction(nameof(Planos));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AlterarPlano(int planoId, CancellationToken ct)
    {
        try
        {
            await servico.AlterarPlanoAsync(tenant.AgenciaId!.Value, planoId, ct);
            await auditoria.RegistrarAsync("assinatura.alterar-plano", tenant.AgenciaId, "Plano", planoId.ToString(), ct: ct);
            TempData["Sucesso"] = "Plano alterado. O novo valor vale a partir da próxima cobrança.";
        }
        catch (ErroGateway e) { TempData["Erro"] = e.Message; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Cancelar(CancellationToken ct)
    {
        try
        {
            await servico.CancelarAsync(tenant.AgenciaId!.Value, ct);
            await auditoria.RegistrarAsync("assinatura.cancelar", tenant.AgenciaId, ct: ct);
            TempData["Sucesso"] = "Assinatura cancelada. Os dados ficam preservados e você pode reativar quando quiser.";
        }
        catch (ErroGateway e) { TempData["Erro"] = e.Message; }
        return RedirectToAction(nameof(Index));
    }

    // ── Somente com o gateway simulado (desenvolvimento) ─────────────────
    private bool SimuladoPermitido => opt.Gateway == "simulado" && !env.IsProduction();

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PagamentoSimulado(string cobranca, CancellationToken ct)
    {
        if (!SimuladoPermitido) return NotFound();
        var pagamento = await db.Pagamentos.FirstOrDefaultAsync(p => p.GatewayPagamentoId == cobranca, ct);
        if (pagamento == null) return NotFound();
        return View(pagamento);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> PagamentoSimulado(string cobranca, string evento, CancellationToken ct)
    {
        if (!SimuladoPermitido) return NotFound();
        var pagamento = await db.Pagamentos.Include(p => p.Assinatura).FirstOrDefaultAsync(p => p.GatewayPagamentoId == cobranca, ct);
        if (pagamento == null) return NotFound();

        var status = evento == "PAYMENT_OVERDUE" ? StatusPagamento.Vencido : StatusPagamento.Recebido;
        var ev = new EventoPagamento($"sim_evt_{Guid.NewGuid():N}", evento == "PAYMENT_OVERDUE" ? "PAYMENT_OVERDUE" : "PAYMENT_RECEIVED",
            pagamento.GatewayPagamentoId, pagamento.Assinatura!.GatewayAssinaturaId, pagamento.Valor, pagamento.Valor,
            status, pagamento.FormaPagamento, DateOnly.FromDateTime(pagamento.Vencimento ?? DateTime.UtcNow),
            status == StatusPagamento.Recebido ? DateTime.UtcNow : null, pagamento.UrlFatura);
        await servico.ProcessarWebhookAsync("simulado", "{}", ev, ev.EventoId, ev.Tipo, ct);
        TempData["Sucesso"] = status == StatusPagamento.Recebido ? "Pagamento simulado confirmado." : "Atraso simulado.";
        return RedirectToAction(nameof(Index));
    }
}
