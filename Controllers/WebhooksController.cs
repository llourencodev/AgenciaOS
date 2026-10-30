using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenciaOS.Services.Cobranca;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgenciaOS.Controllers;

/// <summary>
/// Webhooks dos gateways. Únicos eventos confiáveis para confirmar pagamento.
/// Autenticados por token secreto (comparação em tempo constante) e idempotentes pelo id do evento.
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public class WebhooksController(CobrancaService cobranca, CobrancaOptions opt, ILogger<WebhooksController> logger) : Controller
{
    private const int TamanhoMaximo = 256 * 1024;

    [HttpPost("/webhooks/asaas")]
    [RequestSizeLimit(TamanhoMaximo)]
    public async Task<IActionResult> Asaas(CancellationToken ct)
    {
        if (opt.Gateway != "asaas" || string.IsNullOrEmpty(opt.AsaasWebhookToken)) return NotFound();

        var recebido = Request.Headers["asaas-access-token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(recebido), Encoding.UTF8.GetBytes(opt.AsaasWebhookToken)))
        {
            logger.LogWarning("Webhook Asaas com token inválido de {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        using var leitor = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await leitor.ReadToEndAsync(ct);

        string eventoId, tipo;
        EventoPagamento? ev;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            eventoId = doc.RootElement.GetProperty("id").GetString() ?? throw new JsonException("id ausente");
            tipo = doc.RootElement.GetProperty("event").GetString() ?? "";
            ev = GatewayAsaas.LerWebhook(payload);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Webhook Asaas malformado: {Erro}", e.Message);
            return BadRequest();
        }

        // Exceção aqui → 500 → o Asaas reenvia; o evento fica registrado com o erro para análise
        var r = await cobranca.ProcessarWebhookAsync("asaas", payload, ev, eventoId, tipo, ct);
        return Ok(new { resultado = r.ToString() });
    }
}
