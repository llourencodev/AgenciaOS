using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>Substitui só as chamadas HTTP ao Asaas; nome "asaas" para casar com os webhooks reais.</summary>
public class GatewayAsaasFalso : IGatewayPagamento
{
    public string Nome => "asaas";
    public ConcurrentDictionary<string, CobrancaGateway> PrimeiraCobranca { get; } = new();
    public ConcurrentBag<NovaAssinaturaGateway> Criadas { get; } = [];

    public Task<string> CriarClienteAsync(ClienteGateway c, CancellationToken ct = default) => Task.FromResult($"cus_{Guid.NewGuid():N}");

    public Task<string> CriarAssinaturaAsync(NovaAssinaturaGateway a, CancellationToken ct = default)
    {
        Criadas.Add(a);
        var id = $"sub_{Guid.NewGuid():N}";
        var pay = $"pay_{Guid.NewGuid():N}";
        PrimeiraCobranca[id] = new CobrancaGateway(pay, a.Valor, a.PrimeiroVencimento, StatusPagamento.Pendente, $"https://sandbox.asaas.com/i/{pay}");
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<CobrancaGateway>> ListarCobrancasAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CobrancaGateway>>(PrimeiraCobranca.TryGetValue(id, out var c) ? [c] : []);

    public Task AtualizarValorAsync(string id, decimal v, string d, CancellationToken ct = default) => Task.CompletedTask;
    public Task CancelarAssinaturaAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Fase 4: checkout, webhooks autenticados/idempotentes, bloqueio por inadimplência (cenário 5), limites e planos.</summary>
[Collection("http")]
[Trait("Categoria", "Integracao")]
public class TestesHttpCobranca(AppMultiTenant app)
{
    private record AgenciaTeste(int Id, string Admin, string Equipe, int ClienteId);

    private async Task<AgenciaTeste> CriarAgenciaAsync(SituacaoAssinatura situacao, string? planoCodigo = null, DateTime? testeAte = null)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        using var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistemaTeste();
        var db = escopo.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();
        var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var ag = new Agencia { Nome = $"Agência {sufixo}", Slug = $"ag-{sufixo}", SituacaoAssinatura = situacao, TesteExpiraEm = testeAte, PlanoCodigo = planoCodigo, EmailPrincipal = $"dono-{sufixo}@teste.test" };
        db.Agencias.Add(ag);
        await db.SaveChangesAsync();
        var cliente = new Cliente { AgenciaId = ag.Id, NomeEmpresa = $"Cliente {sufixo}", Responsavel = "r", Email = "e@e" };
        db.Clientes.Add(cliente);
        await db.SaveChangesAsync();

        async Task<string> Usuario(string tipo, TipoUsuario t, string role)
        {
            var email = $"{tipo}-{sufixo}@teste.test";
            var u = new Usuario { UserName = email, Email = email, Nome = tipo, AgenciaId = ag.Id, Tipo = t, EmailConfirmed = true };
            Assert.True((await um.CreateAsync(u, AppMultiTenant.Senha)).Succeeded);
            await um.AddToRoleAsync(u, role);
            return email;
        }
        return new AgenciaTeste(ag.Id, await Usuario("admin", TipoUsuario.Admin, "Admin"), await Usuario("equipe", TipoUsuario.Equipe, "Equipe"), cliente.Id);
    }

    private async Task<Plano> CriarPlanoAsync(int? limiteClientes = null, string? modulos = null)
    {
        using var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistemaTeste();
        var db = escopo.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();
        var p = new Plano { Codigo = $"p-{Guid.NewGuid():N}"[..20], Nome = "Plano teste", Preco = 197m, LimiteClientes = limiteClientes, Modulos = modulos };
        db.Planos.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task<HttpResponseMessage> PostWebhook(string json, string? token = AppMultiTenant.TokenWebhook)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/webhooks/asaas") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token != null) req.Headers.Add("asaas-access-token", token);
        return await app.CreateClient().SendAsync(req);
    }

    private SituacaoAssinatura Situacao(int agenciaId)
    {
        using var db = app.BancoSistema(out var e);
        using (e) return db.Agencias.Single(a => a.Id == agenciaId).SituacaoAssinatura;
    }

    private static bool Bloqueado(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.OriginalString.Contains("/Assinatura/Bloqueada");

    [Fact]
    public async Task Webhook_sem_token_ou_com_token_errado_eh_recusado()
    {
        var json = WebhookAsaas.Json("PAYMENT_RECEIVED", "pay_x", "sub_x", "RECEIVED");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(json, token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhook(json, token: "chute")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhook("{nao é json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(json)).StatusCode); // assinatura desconhecida: aceito e ignorado
    }

    // Cenário 5: agência inadimplente tenta acessar funcionalidades bloqueadas
    [Fact]
    public async Task Cenario5_Agencia_inadimplente_fica_bloqueada_sem_afetar_as_outras_e_volta_ao_pagar()
    {
        var plano = await CriarPlanoAsync();
        var ag = await CriarAgenciaAsync(SituacaoAssinatura.Suspensa, plano.Codigo);
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            db.Assinaturas.Add(new Assinatura { AgenciaId = ag.Id, PlanoId = plano.Id, Situacao = SituacaoAssinatura.Suspensa, Valor = 197m,
                Gateway = "asaas", GatewayAssinaturaId = $"sub_inad_{ag.Id}", ProximoVencimento = DateTime.UtcNow.AddDays(-10) });
            await db.SaveChangesAsync();
        }

        var admin = await app.LoginAsync(ag.Admin);
        var equipe = await app.LoginAsync(ag.Equipe);
        foreach (var url in new[] { "/Dashboard", "/Clientes", $"/Clientes/Details/{ag.ClienteId}", "/Tarefas", "/Financeiro", "/Arquivos", "/Marketing" })
        {
            Assert.True(Bloqueado(await admin.GetAsync(url)), $"admin acessou {url}");
            Assert.True(Bloqueado(await equipe.GetAsync(url)), $"equipe acessou {url}");
        }
        // Escrita também bloqueada
        var criar = await admin.Http.PostAsync("/Clientes/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["NomeEmpresa"] = "x" }));
        Assert.NotEqual(HttpStatusCode.OK, criar.StatusCode);

        // Só a área de assinatura abre; quem não é admin vê a tela de bloqueio
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Assinatura")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await equipe.GetAsync("/Assinatura/Bloqueada")).StatusCode);

        // Outras agências seguem normalmente
        var alpha = await app.LoginAsync("admin@alpha.test");
        Assert.Equal(HttpStatusCode.OK, (await alpha.GetAsync("/Clientes")).StatusCode);

        // Pagamento confirmado pelo gateway (webhook) libera; dados preservados
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(WebhookAsaas.Json("PAYMENT_RECEIVED", $"pay_inad_{ag.Id}", $"sub_inad_{ag.Id}", "RECEIVED"))).StatusCode);
        Assert.Equal(SituacaoAssinatura.Ativa, Situacao(ag.Id));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Clientes/Details/{ag.ClienteId}")).StatusCode);
    }

    [Fact]
    public async Task Teste_vencido_bloqueia_mesmo_antes_da_rotina_rodar()
    {
        var ag = await CriarAgenciaAsync(SituacaoAssinatura.Teste, testeAte: DateTime.UtcNow.AddMinutes(-1));
        var admin = await app.LoginAsync(ag.Admin);
        Assert.True(Bloqueado(await admin.GetAsync("/Clientes")));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Assinatura/Planos")).StatusCode);
    }

    [Fact]
    public async Task Checkout_redireciona_para_a_fatura_e_so_o_webhook_ativa()
    {
        var plano = await CriarPlanoAsync();
        var ag = await CriarAgenciaAsync(SituacaoAssinatura.Teste, testeAte: DateTime.UtcNow.AddDays(10));
        var admin = await app.LoginAsync(ag.Admin);

        var r = await admin.PostFormAsync("/Assinatura/Assinar", "/Assinatura/Planos", new()
        {
            ["planoId"] = plano.Id.ToString(), ["forma"] = "Pix", ["cpfCnpj"] = "12.345.678/0001-90"
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("https://sandbox.asaas.com/i/pay_", r.Headers.Location!.OriginalString);
        Assert.Equal(SituacaoAssinatura.Teste, Situacao(ag.Id)); // voltar do checkout não ativa nada

        string sub, pay;
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            var a = await db.Assinaturas.SingleAsync(x => x.AgenciaId == ag.Id && x.Vigente);
            Assert.Equal(SituacaoAssinatura.PagamentoPendente, a.Situacao);
            Assert.Equal(FormaPagamento.Pix, a.FormaPagamento);
            sub = a.GatewayAssinaturaId!;
            pay = (await db.Pagamentos.SingleAsync(p => p.AssinaturaId == a.Id)).GatewayPagamentoId;
        }

        var json = WebhookAsaas.Json("PAYMENT_RECEIVED", pay, sub, "RECEIVED");
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhook(json)).StatusCode); // reentrega do mesmo evento
        Assert.Equal(SituacaoAssinatura.Ativa, Situacao(ag.Id));
        using (var db = app.BancoSistema(out var e2))
        using (e2) Assert.Equal(1, await db.Pagamentos.CountAsync(p => p.GatewayPagamentoId == pay));

        // Quem não é admin não contrata nem cancela
        var equipe = await app.LoginAsync(ag.Equipe);
        var tentativa = await equipe.PostFormAsync("/Assinatura/Cancelar", "/Tarefas", new());
        Assert.True(tentativa.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    || tentativa.Headers.Location?.OriginalString.Contains("AcessoNegado") == true);
    }

    [Fact]
    public async Task Webhook_de_uma_agencia_nao_altera_outra()
    {
        var plano = await CriarPlanoAsync();
        var a1 = await CriarAgenciaAsync(SituacaoAssinatura.Ativa, plano.Codigo);
        var a2 = await CriarAgenciaAsync(SituacaoAssinatura.Ativa, plano.Codigo);
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            db.Assinaturas.Add(new Assinatura { AgenciaId = a1.Id, PlanoId = plano.Id, Situacao = SituacaoAssinatura.Ativa, Gateway = "asaas", GatewayAssinaturaId = $"sub_a1_{a1.Id}" });
            db.Assinaturas.Add(new Assinatura { AgenciaId = a2.Id, PlanoId = plano.Id, Situacao = SituacaoAssinatura.Ativa, Gateway = "asaas", GatewayAssinaturaId = $"sub_a2_{a2.Id}" });
            await db.SaveChangesAsync();
        }
        await PostWebhook(WebhookAsaas.Json("PAYMENT_OVERDUE", $"pay_a1_{a1.Id}", $"sub_a1_{a1.Id}", "OVERDUE"));
        Assert.Equal(SituacaoAssinatura.Tolerancia, Situacao(a1.Id));
        Assert.Equal(SituacaoAssinatura.Ativa, Situacao(a2.Id));

        // Na tolerância o acesso continua, com aviso
        var admin = await app.LoginAsync(a1.Admin);
        var dash = await admin.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.OK, dash.StatusCode);
        Assert.Contains("pagamento em aberto", await dash.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Limites_e_modulos_do_plano_sao_aplicados_no_servidor()
    {
        var plano = await CriarPlanoAsync(limiteClientes: 1, modulos: "Clientes,Tarefas");
        var ag = await CriarAgenciaAsync(SituacaoAssinatura.Ativa, plano.Codigo); // já tem 1 cliente
        var admin = await app.LoginAsync(ag.Admin);

        await admin.PostFormAsync("/Clientes/Create", "/Clientes/Create", new()
        {
            ["NomeEmpresa"] = "Cliente além do limite", ["Responsavel"] = "x", ["Email"] = "x@x.com", ["Ativo"] = "true"
        });
        using (var db = app.BancoSistema(out var e))
        using (e) Assert.False(await db.Clientes.AnyAsync(c => c.NomeEmpresa == "Cliente além do limite"));

        var fin = await admin.GetAsync("/Financeiro");
        Assert.Equal(HttpStatusCode.Redirect, fin.StatusCode);
        Assert.Contains("/Assinatura/ModuloIndisponivel", fin.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Tarefas")).StatusCode);
    }

    [Fact]
    public async Task Planos_sao_cadastrados_pelo_superadmin_sem_codigo_e_agencia_nao_acessa()
    {
        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        var codigo = $"plano-{Guid.NewGuid():N}"[..20];
        var r = await super.PostFormAsync("/Plataforma/Plano", "/Plataforma/Plano", new()
        {
            ["Id"] = "0", ["Nome"] = "Plano Novo", ["Codigo"] = codigo, ["Preco"] = "149.90", ["Periodicidade"] = "Mensal",
            ["LimiteUsuarios"] = "5", ["Ativo"] = "true", ["Ordem"] = "1", ["modulos"] = "Clientes"
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            var p = await db.Planos.SingleAsync(x => x.Codigo == codigo);
            Assert.Equal(5, p.LimiteUsuarios);
            Assert.Equal("Clientes", p.Modulos);
        }

        var alpha = await app.LoginAsync("admin@alpha.test");
        var negado = await alpha.GetAsync("/Plataforma/Planos");
        Assert.Contains("AcessoNegado", negado.Headers.Location!.OriginalString);
    }
}

internal static class EscopoTesteExtensions
{
    public static IServiceScope CriarEscopoSistemaTeste(this IServiceScopeFactory f) =>
        AgenciaOS.Services.Tenancy.TenantExtensions.CriarEscopoSistema(f);
}
