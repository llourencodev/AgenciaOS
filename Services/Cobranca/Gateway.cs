using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenciaOS.Models;

namespace AgenciaOS.Services.Cobranca;

public record ClienteGateway(string Nome, string Email, string CpfCnpj, string? Telefone, string ReferenciaExterna);

public record NovaAssinaturaGateway(string ClienteId, FormaPagamento Forma, decimal Valor, DateOnly PrimeiroVencimento,
    Periodicidade Ciclo, string Descricao, string ReferenciaExterna);

public record CobrancaGateway(string Id, decimal Valor, DateOnly? Vencimento, StatusPagamento Status, string? UrlFatura);

/// <summary>Evento de pagamento já traduzido do formato do gateway.</summary>
public record EventoPagamento(
    string EventoId, string Tipo, string PagamentoId, string? AssinaturaId, decimal Valor, decimal? ValorLiquido,
    StatusPagamento Status, FormaPagamento Forma, DateOnly? Vencimento, DateTime? PagoEm, string? UrlFatura);

/// <summary>Gateway de pagamentos. A confirmação de pagamento chega SEMPRE por webhook, nunca pelo retorno do navegador.</summary>
public interface IGatewayPagamento
{
    string Nome { get; }
    Task<string> CriarClienteAsync(ClienteGateway cliente, CancellationToken ct = default);
    Task<string> CriarAssinaturaAsync(NovaAssinaturaGateway assinatura, CancellationToken ct = default);
    Task<IReadOnlyList<CobrancaGateway>> ListarCobrancasAsync(string assinaturaId, CancellationToken ct = default);
    Task AtualizarValorAsync(string assinaturaId, decimal valor, string descricao, CancellationToken ct = default);
    Task CancelarAssinaturaAsync(string assinaturaId, CancellationToken ct = default);
}

public class CobrancaOptions
{
    /// <summary>"asaas" ou "simulado" (desenvolvimento/testes; nunca em produção).</summary>
    public string Gateway { get; set; } = "simulado";
    public string? AsaasApiKey { get; set; }
    public string AsaasBaseUrl { get; set; } = "https://api-sandbox.asaas.com/v3";
    /// <summary>Token configurado no painel do Asaas para o webhook (header asaas-access-token).</summary>
    public string? AsaasWebhookToken { get; set; }
    public int DiasTolerancia { get; set; } = 7;
    public bool WorkerHabilitado { get; set; } = true;

    public static CobrancaOptions Carregar(IConfiguration cfg)
    {
        var chave = cfg["ASAAS_API_KEY"];
        return new()
        {
            Gateway = (cfg["PAYMENT_GATEWAY"] ?? "simulado").Trim().ToLowerInvariant(),
            AsaasApiKey = chave,
            // Chaves de produção começam com $aact_prod_; as de sandbox com $aact_hmlg_
            AsaasBaseUrl = cfg["ASAAS_BASE_URL"]
                ?? (chave?.StartsWith("$aact_prod_") == true ? "https://api.asaas.com/v3" : "https://api-sandbox.asaas.com/v3"),
            AsaasWebhookToken = cfg["ASAAS_WEBHOOK_TOKEN"],
            DiasTolerancia = int.TryParse(cfg["BILLING_TOLERANCIA_DIAS"], out var d) && d is >= 0 and <= 60 ? d : 7,
            WorkerHabilitado = !string.Equals(cfg["BILLING_WORKER_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>Asaas (https://docs.asaas.com). Checkout hospedado pelo Asaas: dados de cartão nunca passam pelo AgenciaOS.</summary>
public class GatewayAsaas(HttpClient http, CobrancaOptions opt) : IGatewayPagamento
{
    public const string NomeCliente = "asaas";
    public string Nome => "asaas";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private HttpRequestMessage Req(HttpMethod metodo, string caminho, object? corpo = null)
    {
        var req = new HttpRequestMessage(metodo, $"{opt.AsaasBaseUrl.TrimEnd('/')}/{caminho.TrimStart('/')}");
        req.Headers.Add("access_token", opt.AsaasApiKey);
        req.Headers.UserAgent.ParseAdd("AgenciaOS/1.0");
        if (corpo != null) req.Content = JsonContent.Create(corpo, options: Json);
        return req;
    }

    private async Task<JsonElement> EnviarAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var resp = await http.SendAsync(req, ct))
        {
            var texto = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // A mensagem de erro do Asaas não contém dados sensíveis; útil para o usuário corrigir (ex.: CPF/CNPJ inválido)
                string? detalhe = null;
                try { detalhe = JsonDocument.Parse(texto).RootElement.GetProperty("errors")[0].GetProperty("description").GetString(); } catch { }
                throw new ErroGateway(detalhe ?? $"O gateway recusou a operação ({(int)resp.StatusCode}).");
            }
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(texto) ? "{}" : texto).RootElement.Clone();
        }
    }

    public static string BillingType(FormaPagamento f) => f switch
    {
        FormaPagamento.Pix => "PIX",
        FormaPagamento.Boleto => "BOLETO",
        FormaPagamento.CartaoCredito => "CREDIT_CARD",
        _ => "UNDEFINED"
    };

    public async Task<string> CriarClienteAsync(ClienteGateway c, CancellationToken ct = default)
    {
        var r = await EnviarAsync(Req(HttpMethod.Post, "customers", new
        {
            name = c.Nome, email = c.Email, cpfCnpj = SoDigitos(c.CpfCnpj),
            mobilePhone = string.IsNullOrWhiteSpace(c.Telefone) ? null : SoDigitos(c.Telefone),
            externalReference = c.ReferenciaExterna, notificationDisabled = false
        }), ct);
        return r.GetProperty("id").GetString()!;
    }

    public async Task<string> CriarAssinaturaAsync(NovaAssinaturaGateway a, CancellationToken ct = default)
    {
        var r = await EnviarAsync(Req(HttpMethod.Post, "subscriptions", new
        {
            customer = a.ClienteId, billingType = BillingType(a.Forma), value = a.Valor,
            nextDueDate = a.PrimeiroVencimento.ToString("yyyy-MM-dd"),
            cycle = a.Ciclo == Periodicidade.Anual ? "YEARLY" : "MONTHLY",
            description = a.Descricao, externalReference = a.ReferenciaExterna
        }), ct);
        return r.GetProperty("id").GetString()!;
    }

    public async Task<IReadOnlyList<CobrancaGateway>> ListarCobrancasAsync(string assinaturaId, CancellationToken ct = default)
    {
        var r = await EnviarAsync(Req(HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(assinaturaId)}/payments"), ct);
        return r.GetProperty("data").EnumerateArray().Select(p => new CobrancaGateway(
            p.GetProperty("id").GetString()!,
            p.GetProperty("value").GetDecimal(),
            Data(p, "dueDate"),
            Status(p.GetProperty("status").GetString()),
            p.TryGetProperty("invoiceUrl", out var u) ? u.GetString() : null)).ToList();
    }

    public async Task AtualizarValorAsync(string assinaturaId, decimal valor, string descricao, CancellationToken ct = default) =>
        await EnviarAsync(Req(HttpMethod.Post, $"subscriptions/{Uri.EscapeDataString(assinaturaId)}",
            new { value = valor, description = descricao, updatePendingPayments = true }), ct);

    public async Task CancelarAssinaturaAsync(string assinaturaId, CancellationToken ct = default) =>
        await EnviarAsync(Req(HttpMethod.Delete, $"subscriptions/{Uri.EscapeDataString(assinaturaId)}"), ct);

    // ── Webhook ─────────────────────────────────────────────────────────
    /// <summary>Traduz o JSON do webhook do Asaas. Retorna null para eventos que não são de cobrança.</summary>
    public static EventoPagamento? LerWebhook(string json)
    {
        var raiz = JsonDocument.Parse(json).RootElement;
        var id = raiz.GetProperty("id").GetString()!;
        var tipo = raiz.GetProperty("event").GetString()!;
        if (!raiz.TryGetProperty("payment", out var p) || p.ValueKind != JsonValueKind.Object) return null;

        DateTime? pagoEm = null;
        foreach (var campo in new[] { "clientPaymentDate", "paymentDate", "confirmedDate" })
            if (Data(p, campo) is { } d) { pagoEm = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc); break; }

        return new EventoPagamento(
            id, tipo,
            p.GetProperty("id").GetString()!,
            p.TryGetProperty("subscription", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
            p.TryGetProperty("value", out var v) ? v.GetDecimal() : 0,
            p.TryGetProperty("netValue", out var nv) && nv.ValueKind == JsonValueKind.Number ? nv.GetDecimal() : null,
            tipo == "PAYMENT_DELETED" ? StatusPagamento.Removido : Status(p.TryGetProperty("status", out var st) ? st.GetString() : null),
            Forma(p.TryGetProperty("billingType", out var bt) ? bt.GetString() : null),
            Data(p, "dueDate"), pagoEm,
            p.TryGetProperty("invoiceUrl", out var u) ? u.GetString() : null);
    }

    public static StatusPagamento Status(string? s) => s switch
    {
        "CONFIRMED" => StatusPagamento.Confirmado,
        "RECEIVED" or "RECEIVED_IN_CASH" or "DUNNING_RECEIVED" => StatusPagamento.Recebido,
        "OVERDUE" => StatusPagamento.Vencido,
        "REFUNDED" or "REFUND_REQUESTED" or "REFUND_IN_PROGRESS" => StatusPagamento.Estornado,
        "CHARGEBACK_REQUESTED" or "CHARGEBACK_DISPUTE" or "AWAITING_CHARGEBACK_REVERSAL" => StatusPagamento.Contestado,
        _ => StatusPagamento.Pendente
    };

    private static FormaPagamento Forma(string? b) => b switch
    {
        "PIX" => FormaPagamento.Pix, "BOLETO" => FormaPagamento.Boleto, "CREDIT_CARD" => FormaPagamento.CartaoCredito, _ => FormaPagamento.Indefinida
    };

    private static DateOnly? Data(JsonElement e, string campo) =>
        e.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string SoDigitos(string s) => new(s.Where(char.IsDigit).ToArray());
}

/// <summary>
/// Gateway de mentira para desenvolvimento e testes. A "fatura" é uma página local que simula o pagamento
/// disparando o mesmo processamento de webhook. Bloqueado em produção (ver Program.cs).
/// </summary>
public class GatewaySimulado : IGatewayPagamento
{
    public string Nome => "simulado";
    private readonly ConcurrentDictionary<string, List<CobrancaGateway>> _cobrancas = new();
    public ConcurrentBag<string> Canceladas { get; } = [];

    public Task<string> CriarClienteAsync(ClienteGateway cliente, CancellationToken ct = default) =>
        Task.FromResult($"sim_cus_{Guid.NewGuid():N}");

    public Task<string> CriarAssinaturaAsync(NovaAssinaturaGateway a, CancellationToken ct = default)
    {
        var id = $"sim_sub_{Guid.NewGuid():N}";
        var pagamento = $"sim_pay_{Guid.NewGuid():N}";
        _cobrancas[id] = [new CobrancaGateway(pagamento, a.Valor, a.PrimeiroVencimento, StatusPagamento.Pendente,
            $"/Assinatura/PagamentoSimulado?cobranca={pagamento}")];
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<CobrancaGateway>> ListarCobrancasAsync(string assinaturaId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CobrancaGateway>>(_cobrancas.TryGetValue(assinaturaId, out var l) ? l : []);

    public Task AtualizarValorAsync(string assinaturaId, decimal valor, string descricao, CancellationToken ct = default) => Task.CompletedTask;

    public Task CancelarAssinaturaAsync(string assinaturaId, CancellationToken ct = default)
    {
        Canceladas.Add(assinaturaId);
        return Task.CompletedTask;
    }
}

public class ErroGateway(string mensagem) : Exception(mensagem);

/// <summary>Produção sem PAYMENT_GATEWAY configurado: nenhuma cobrança é criada.</summary>
public class GatewayDesabilitado : IGatewayPagamento
{
    public string Nome => "desabilitado";
    private static ErroGateway Erro => new("A cobrança ainda não está configurada nesta instalação. Fale com o suporte do AgenciaOS.");
    public Task<string> CriarClienteAsync(ClienteGateway c, CancellationToken ct = default) => throw Erro;
    public Task<string> CriarAssinaturaAsync(NovaAssinaturaGateway a, CancellationToken ct = default) => throw Erro;
    public Task<IReadOnlyList<CobrancaGateway>> ListarCobrancasAsync(string id, CancellationToken ct = default) => throw Erro;
    public Task AtualizarValorAsync(string id, decimal v, string d, CancellationToken ct = default) => throw Erro;
    public Task CancelarAssinaturaAsync(string id, CancellationToken ct = default) => throw Erro;
}

public static class CobrancaServiceCollectionExtensions
{
    public static IServiceCollection AddCobranca(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env)
    {
        var opt = CobrancaOptions.Carregar(cfg);
        services.AddSingleton(opt);
        switch (opt.Gateway)
        {
            case "asaas":
                if (string.IsNullOrWhiteSpace(opt.AsaasApiKey) || string.IsNullOrWhiteSpace(opt.AsaasWebhookToken))
                    throw new InvalidOperationException("PAYMENT_GATEWAY=asaas exige ASAAS_API_KEY e ASAAS_WEBHOOK_TOKEN.");
                // Sem loggers do HttpClient: não registrar cabeçalhos/corpos de cobrança
                services.AddHttpClient<IGatewayPagamento, GatewayAsaas>(GatewayAsaas.NomeCliente, c => c.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
                break;
            case "simulado" when !env.IsProduction():
                services.AddSingleton<GatewaySimulado>();
                services.AddSingleton<IGatewayPagamento>(sp => sp.GetRequiredService<GatewaySimulado>());
                break;
            case "simulado" or "desabilitado":
                // Produção sem gateway configurado: o app sobe, mas contratar plano mostra erro (e o log avisa)
                opt.Gateway = "desabilitado";
                services.AddSingleton<IGatewayPagamento, GatewayDesabilitado>();
                break;
            default:
                throw new InvalidOperationException($"PAYMENT_GATEWAY desconhecido: {opt.Gateway}");
        }
        services.AddMemoryCache();
        services.AddScoped<CobrancaService>();
        services.AddScoped<LimitesPlanoService>();
        services.AddHostedService<AssinaturasWorker>();
        return services;
    }
}
