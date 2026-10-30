using System.Net;
using System.Text;
using System.Text.Json;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>Monta um payload de webhook no formato do Asaas.</summary>
internal static class WebhookAsaas
{
    public static string Json(string evento, string pagamentoId, string? assinaturaId, string status, decimal valor = 197m,
        string vencimento = "2026-11-01", string? eventoId = null) =>
        JsonSerializer.Serialize(new
        {
            id = eventoId ?? $"evt_{Guid.NewGuid():N}",
            @event = evento,
            dateCreated = "2026-10-30 10:00:00",
            payment = new
            {
                @object = "payment", id = pagamentoId, customer = "cus_1", subscription = assinaturaId,
                value = valor, netValue = valor - 2, billingType = "PIX", status, dueDate = vencimento,
                paymentDate = status is "RECEIVED" or "CONFIRMED" ? "2026-11-01" : null,
                invoiceUrl = $"https://sandbox.asaas.com/i/{pagamentoId}"
            }
        });
}

public class TestesGatewayAsaas
{
    private sealed class Captura : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Corpo)> Pedidos { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Resposta { get; set; } = _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"x\"}") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Pedidos.Add((r, r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct)));
            return Resposta(r);
        }
    }

    [Fact]
    public async Task Envia_autenticacao_e_campos_da_api_v3()
    {
        var h = new Captura { Resposta = _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"sub_123\"}") } };
        var gw = new GatewayAsaas(new HttpClient(h), new CobrancaOptions { AsaasApiKey = "$aact_hmlg_teste", AsaasBaseUrl = "https://api-sandbox.asaas.com/v3" });

        var id = await gw.CriarAssinaturaAsync(new NovaAssinaturaGateway("cus_1", FormaPagamento.Pix, 197.5m, new DateOnly(2026, 11, 1),
            Periodicidade.Mensal, "AgenciaOS - Pro", "agencia-7"));

        Assert.Equal("sub_123", id);
        var (req, corpo) = h.Pedidos.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api-sandbox.asaas.com/v3/subscriptions", req.RequestUri!.ToString());
        Assert.Equal("$aact_hmlg_teste", req.Headers.GetValues("access_token").Single());
        Assert.Contains("AgenciaOS", req.Headers.UserAgent.ToString());
        using var json = JsonDocument.Parse(corpo);
        Assert.Equal("PIX", json.RootElement.GetProperty("billingType").GetString());
        Assert.Equal("MONTHLY", json.RootElement.GetProperty("cycle").GetString());
        Assert.Equal("2026-11-01", json.RootElement.GetProperty("nextDueDate").GetString());
        Assert.Equal(197.5m, json.RootElement.GetProperty("value").GetDecimal());
        Assert.Equal("agencia-7", json.RootElement.GetProperty("externalReference").GetString());
    }

    [Fact]
    public async Task Erro_do_gateway_vira_mensagem_legivel()
    {
        var h = new Captura { Resposta = _ => new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"errors\":[{\"code\":\"invalid_cpfCnpj\",\"description\":\"O CPF/CNPJ informado é inválido.\"}]}") } };
        var gw = new GatewayAsaas(new HttpClient(h), new CobrancaOptions { AsaasApiKey = "k" });
        var e = await Assert.ThrowsAsync<ErroGateway>(() => gw.CriarClienteAsync(new ClienteGateway("A", "a@a", "123", null, "agencia-1")));
        Assert.Equal("O CPF/CNPJ informado é inválido.", e.Message);
    }

    [Fact]
    public void Le_webhook_de_pagamento()
    {
        var ev = GatewayAsaas.LerWebhook(WebhookAsaas.Json("PAYMENT_RECEIVED", "pay_1", "sub_1", "RECEIVED", eventoId: "evt_9"))!;
        Assert.Equal("evt_9", ev.EventoId);
        Assert.Equal("pay_1", ev.PagamentoId);
        Assert.Equal("sub_1", ev.AssinaturaId);
        Assert.Equal(StatusPagamento.Recebido, ev.Status);
        Assert.Equal(FormaPagamento.Pix, ev.Forma);
        Assert.Equal(new DateOnly(2026, 11, 1), ev.Vencimento);
        Assert.NotNull(ev.PagoEm);
        Assert.Null(GatewayAsaas.LerWebhook("{\"id\":\"evt_x\",\"event\":\"TRANSFER_DONE\",\"transfer\":{}}"));
    }

    [Theory]
    [InlineData(SituacaoAssinatura.Ativa, null, false)]
    [InlineData(SituacaoAssinatura.Tolerancia, null, false)]
    [InlineData(SituacaoAssinatura.Suspensa, null, true)]
    [InlineData(SituacaoAssinatura.Cancelada, null, true)]
    [InlineData(SituacaoAssinatura.Teste, 1, false)]
    [InlineData(SituacaoAssinatura.Teste, -1, true)]
    [InlineData(SituacaoAssinatura.PagamentoPendente, 1, false)]
    [InlineData(SituacaoAssinatura.PagamentoPendente, null, true)]
    public void Regras_de_bloqueio(SituacaoAssinatura s, int? diasTeste, bool bloqueada)
    {
        var agora = DateTime.UtcNow;
        Assert.Equal(bloqueada, CobrancaService.Bloqueada(s, diasTeste is int d ? agora.AddDays(d) : null, agora));
    }
}

public class TestesCicloAssinatura
{
    private sealed class Cenario : IDisposable
    {
        public BancoTeste Banco { get; } = new();
        public GatewaySimulado Gateway { get; } = new();
        public EnviadorEmailMemoria Emails { get; } = new();
        public int PlanoId { get; }

        public Cenario()
        {
            using var db = Banco.ContextoSistema();
            var plano = new Plano { Codigo = "pro", Nome = "Pro", Preco = 197m };
            db.Planos.Add(plano);
            var ag = db.Agencias.Single(a => a.Id == 1);
            ag.SituacaoAssinatura = SituacaoAssinatura.Teste;
            ag.TesteExpiraEm = DateTime.UtcNow.AddDays(5);
            ag.EmailPrincipal = "dono@alpha.test";
            db.SaveChanges();
            PlanoId = plano.Id;
        }

        /// <summary>Serviço como numa requisição da agência 1 (ou do webhook, que muda para Sistema sozinho).</summary>
        public (CobrancaService Servico, Data.ApplicationDbContext Db) Servico(int? agencia = 1)
        {
            var t = new TenantContext();
            if (agencia is int a) t.UsarAgencia(a); else t.UsarSistema();
            var db = Banco.Contexto(t);
            return (new CobrancaService(db, Gateway, new CobrancaOptions { DiasTolerancia = 7 }, t, Emails, NullLogger<CobrancaService>.Instance), db);
        }

        public async Task<string> AssinarAsync()
        {
            var (s, db) = Servico();
            using (db) await s.AssinarAsync(1, PlanoId, FormaPagamento.Pix, "12.345.678/0001-90");
            using var sis = Banco.ContextoSistema();
            return (await sis.Assinaturas.SingleAsync(a => a.Vigente)).GatewayAssinaturaId!;
        }

        public async Task<CobrancaService.ResultadoWebhook> WebhookAsync(string evento, string pagamento, string? assinatura, string status, string? eventoId = null, string venc = "2026-11-01")
        {
            var json = WebhookAsaas.Json(evento, pagamento, assinatura, status, eventoId: eventoId, vencimento: venc);
            var ev = GatewayAsaas.LerWebhook(json)!;
            var (s, db) = Servico(null);
            using (db) return await s.ProcessarWebhookAsync("simulado", json, ev, ev.EventoId, ev.Tipo);
        }

        public SituacaoAssinatura SituacaoAgencia()
        {
            using var db = Banco.ContextoSistema();
            return db.Agencias.Single(a => a.Id == 1).SituacaoAssinatura;
        }

        public void Dispose() => Banco.Dispose();
    }

    [Fact]
    public async Task Assinar_durante_o_teste_mantem_teste_ate_o_pagamento_confirmado_pelo_webhook()
    {
        using var c = new Cenario();
        var sub = await c.AssinarAsync();
        Assert.Equal(SituacaoAssinatura.Teste, c.SituacaoAgencia()); // retorno do navegador não ativa nada

        using (var db = c.Banco.ContextoSistema())
        {
            var p = await db.Pagamentos.SingleAsync();
            Assert.Equal(StatusPagamento.Pendente, p.Status);
            Assert.StartsWith("/Assinatura/PagamentoSimulado", p.UrlFatura);
            Assert.Equal("12345678000190", (await db.Agencias.SingleAsync(a => a.Id == 1)).Cnpj);
        }

        var pagamentoId = (await c.Gateway.ListarCobrancasAsync(sub)).Single().Id;
        Assert.Equal(CobrancaService.ResultadoWebhook.Processado, await c.WebhookAsync("PAYMENT_RECEIVED", pagamentoId, sub, "RECEIVED", "evt_1"));
        Assert.Equal(SituacaoAssinatura.Ativa, c.SituacaoAgencia());
        Assert.Contains(c.Emails.Enviadas, m => m.Assunto.Contains("Pagamento confirmado"));

        using var sis = c.Banco.ContextoSistema();
        var a = await sis.Assinaturas.SingleAsync(x => x.Vigente);
        Assert.Equal(new DateTime(2026, 12, 1), a.ProximoVencimento!.Value.Date);
        Assert.True((await sis.Pagamentos.SingleAsync()).Quitado);
    }

    [Fact]
    public async Task Webhook_repetido_nao_tem_efeito_duplicado()
    {
        using var c = new Cenario();
        var sub = await c.AssinarAsync();
        var pag = (await c.Gateway.ListarCobrancasAsync(sub)).Single().Id;

        Assert.Equal(CobrancaService.ResultadoWebhook.Processado, await c.WebhookAsync("PAYMENT_RECEIVED", pag, sub, "RECEIVED", "evt_dup"));
        var emails = c.Emails.Enviadas.Count;
        Assert.Equal(CobrancaService.ResultadoWebhook.Duplicado, await c.WebhookAsync("PAYMENT_RECEIVED", pag, sub, "RECEIVED", "evt_dup"));
        Assert.Equal(emails, c.Emails.Enviadas.Count);

        using var db = c.Banco.ContextoSistema();
        Assert.Equal(1, await db.EventosWebhook.CountAsync());
        Assert.Equal(1, await db.Pagamentos.CountAsync());
    }

    [Fact]
    public async Task Atraso_da_tolerancia_depois_suspende_e_pagamento_reativa_sem_perder_dados()
    {
        using var c = new Cenario();
        var sub = await c.AssinarAsync();
        var pag1 = (await c.Gateway.ListarCobrancasAsync(sub)).Single().Id;
        await c.WebhookAsync("PAYMENT_RECEIVED", pag1, sub, "RECEIVED");
        using (var db = c.Banco.NovoContexto(1))
        {
            db.Clientes.Add(new Cliente { NomeEmpresa = "Cliente que não pode sumir", Responsavel = "r", Email = "e" });
            await db.SaveChangesAsync();
        }

        // Mês seguinte: cobrança vence
        await c.WebhookAsync("PAYMENT_CREATED", "pay_dez", sub, "PENDING", venc: "2026-12-01");
        await c.WebhookAsync("PAYMENT_OVERDUE", "pay_dez", sub, "OVERDUE", venc: "2026-12-01");
        Assert.Equal(SituacaoAssinatura.Tolerancia, c.SituacaoAgencia());
        Assert.Contains(c.Emails.Enviadas, m => m.Assunto.Contains("atraso"));

        // Dentro da tolerância nada muda; depois dela, suspende
        var (s1, db1) = c.Servico(null);
        using (db1) Assert.Equal(0, await s1.AtualizarSituacoesAsync(DateTime.UtcNow.AddDays(3)));
        var (s2, db2) = c.Servico(null);
        using (db2) Assert.Equal(1, await s2.AtualizarSituacoesAsync(DateTime.UtcNow.AddDays(8)));
        Assert.Equal(SituacaoAssinatura.Suspensa, c.SituacaoAgencia());

        // Paga atrasado: reativa, dados intactos
        await c.WebhookAsync("PAYMENT_RECEIVED", "pay_dez", sub, "RECEIVED", venc: "2026-12-01");
        Assert.Equal(SituacaoAssinatura.Ativa, c.SituacaoAgencia());
        using var sis = c.Banco.ContextoSistema();
        Assert.True(await sis.Clientes.AnyAsync(x => x.NomeEmpresa == "Cliente que não pode sumir"));
    }

    [Fact]
    public async Task Evento_fora_de_ordem_nao_desfaz_pagamento_confirmado()
    {
        using var c = new Cenario();
        var sub = await c.AssinarAsync();
        var pag = (await c.Gateway.ListarCobrancasAsync(sub)).Single().Id;
        await c.WebhookAsync("PAYMENT_RECEIVED", pag, sub, "RECEIVED");
        await c.WebhookAsync("PAYMENT_OVERDUE", pag, sub, "OVERDUE"); // chegou atrasado

        Assert.Equal(SituacaoAssinatura.Ativa, c.SituacaoAgencia());
        using var db = c.Banco.ContextoSistema();
        Assert.Equal(StatusPagamento.Recebido, (await db.Pagamentos.SingleAsync()).Status);
    }

    [Fact]
    public async Task Evento_de_assinatura_desconhecida_e_ignorado()
    {
        using var c = new Cenario();
        Assert.Equal(CobrancaService.ResultadoWebhook.Ignorado, await c.WebhookAsync("PAYMENT_RECEIVED", "pay_x", "sub_de_outro_sistema", "RECEIVED"));
        Assert.Equal(SituacaoAssinatura.Teste, c.SituacaoAgencia());
    }

    [Fact]
    public async Task Teste_vencido_suspende_e_cancelamento_bloqueia_so_depois_do_periodo_pago()
    {
        using var c = new Cenario();
        var (s, db) = c.Servico(null);
        using (db) await s.AtualizarSituacoesAsync(DateTime.UtcNow.AddDays(6));
        Assert.Equal(SituacaoAssinatura.Suspensa, c.SituacaoAgencia());

        var sub = await c.AssinarAsync();
        Assert.Equal(SituacaoAssinatura.PagamentoPendente, c.SituacaoAgencia()); // suspensa assinando: aguarda pagamento
        var pag = (await c.Gateway.ListarCobrancasAsync(sub)).Single().Id;
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        await c.WebhookAsync("PAYMENT_RECEIVED", pag, sub, "RECEIVED", venc: hoje);
        Assert.Equal(SituacaoAssinatura.Ativa, c.SituacaoAgencia());

        var (s2, db2) = c.Servico();
        using (db2) await s2.CancelarAsync(1);
        Assert.Contains(sub, c.Gateway.Canceladas);
        Assert.Equal(SituacaoAssinatura.Ativa, c.SituacaoAgencia()); // período pago continua valendo

        var (s3, db3) = c.Servico(null);
        using (db3) await s3.AtualizarSituacoesAsync(DateTime.UtcNow.AddDays(32));
        Assert.Equal(SituacaoAssinatura.Cancelada, c.SituacaoAgencia());
    }
}
