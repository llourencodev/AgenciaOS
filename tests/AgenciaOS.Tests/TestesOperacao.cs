using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Operacao;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>Gera códigos TOTP (RFC 6238) como um aplicativo autenticador.</summary>
internal static class Totp
{
    public static string Codigo(string chaveBase32, DateTime? quando = null)
    {
        var chave = Base32(chaveBase32);
        var passo = ((quando ?? DateTime.UtcNow) - DateTime.UnixEpoch).Ticks / TimeSpan.FromSeconds(30).Ticks;
        var contador = BitConverter.GetBytes(passo);
        if (BitConverter.IsLittleEndian) Array.Reverse(contador);
        var hash = HMACSHA1.HashData(chave, contador);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string s)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            buffer = (buffer << 5) | alfabeto.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}

public class TestesRetencao
{
    [Fact]
    public async Task Remove_so_o_que_passou_do_prazo()
    {
        using var banco = new BancoTeste();
        var agora = DateTime.UtcNow;
        using (var db = banco.ContextoSistema())
        {
            db.RegistrosAuditoria.AddRange(
                new RegistroAuditoria { Acao = "conta.login", Quando = agora.AddMonths(-7) },          // remove (6 meses)
                new RegistroAuditoria { Acao = "conta.login-falhou", Quando = agora.AddMonths(-8) },   // remove
                new RegistroAuditoria { Acao = "conta.login", Quando = agora.AddMonths(-2) },          // fica
                new RegistroAuditoria { Acao = "plataforma.suspender-agencia", Quando = agora.AddYears(-2) }, // fica (5 anos)
                new RegistroAuditoria { Acao = "plataforma.suspender-agencia", Quando = agora.AddYears(-6) }); // remove
            db.EventosWebhook.AddRange(
                new EventoWebhook { Gateway = "asaas", EventoId = "velho", RecebidoEm = agora.AddYears(-2), ProcessadoEm = agora.AddYears(-2) },
                new EventoWebhook { Gateway = "asaas", EventoId = "velho-com-falha", RecebidoEm = agora.AddYears(-2) }, // não processado: fica
                new EventoWebhook { Gateway = "asaas", EventoId = "novo", RecebidoEm = agora, ProcessadoEm = agora });
            db.Clientes.Add(new Cliente { AgenciaId = 1, NomeEmpresa = "Dado de negócio antigo", Responsavel = "r", Email = "e", CriadoEm = agora.AddYears(-10) });
            await db.SaveChangesAsync();
        }

        var t = new TenantContext();
        using (var db = banco.Contexto(t))
        {
            var r = await new RetencaoService(db, t).ExecutarAsync(agora);
            Assert.Equal(2, r.LoginsRemovidos);
            Assert.Equal(1, r.AuditoriaRemovida);
            Assert.Equal(1, r.WebhooksRemovidos);
        }

        using var sis = banco.ContextoSistema();
        Assert.Equal(2, await sis.RegistrosAuditoria.CountAsync());
        Assert.Equal(["novo", "velho-com-falha"], await sis.EventosWebhook.OrderBy(e => e.EventoId).Select(e => e.EventoId).ToListAsync());
        Assert.True(await sis.Clientes.AnyAsync(c => c.NomeEmpresa == "Dado de negócio antigo")); // nunca apaga dado de negócio
    }

    [Fact]
    public void Homologacao_nunca_usa_o_gateway_simulado()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PAYMENT_GATEWAY"] = "simulado" }).Build();
        foreach (var ambiente in new[] { "Staging", "Production" })
        {
            var s = new ServiceCollection();
            s.AddCobranca(cfg, new HostingEnvironment { EnvironmentName = ambiente });
            using var sp = s.BuildServiceProvider();
            Assert.IsType<GatewayDesabilitado>(sp.GetRequiredService<IGatewayPagamento>());
        }
        var dev = new ServiceCollection();
        dev.AddCobranca(cfg, new HostingEnvironment { EnvironmentName = "Development" });
        using var spDev = dev.BuildServiceProvider();
        Assert.IsType<GatewaySimulado>(spDev.GetRequiredService<IGatewayPagamento>());
    }
}

/// <summary>Fase 6: 2FA, health checks, página de status.</summary>
[Collection("http")]
[Trait("Categoria", "Integracao")]
public class TestesHttpOperacao(AppMultiTenant app)
{
    private async Task<string> CriarUsuarioAsync()
    {
        var email = $"2fa-{Guid.NewGuid():N}"[..12] + "@teste.test";
        using var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema();
        var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var u = new Usuario { UserName = email, Email = email, Nome = "Dois Fatores", AgenciaId = app.AgenciaAlpha, EmailConfirmed = true, Tipo = TipoUsuario.Equipe };
        Assert.True((await um.CreateAsync(u, AppMultiTenant.Senha)).Succeeded);
        await um.AddToRoleAsync(u, "Equipe");
        return email;
    }

    private Navegador Novo() => new(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    private static bool Autenticou(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.Redirect && r.Headers.TryGetValues("Set-Cookie", out var c) && c.Any(x => x.StartsWith(".AspNetCore.Identity.Application="));

    [Fact]
    public async Task Dois_fatores_ativacao_login_recuperacao_e_desativacao()
    {
        var email = await CriarUsuarioAsync();
        var nav = await app.LoginAsync(email);

        // Ativação: código errado não ativa; código do app ativa e devolve 10 códigos de recuperação
        var html = await (await nav.GetAsync("/Conta/DoisFatores")).Content.ReadAsStringAsync();
        var chave = Regex.Match(html, "<code style=\"font-size:15px;\">([a-z0-9 ]+)</code>").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(chave));
        Assert.Contains("alert-danger", await (await nav.PostFormAsync("/Conta/AtivarDoisFatores", "/Conta/DoisFatores", new() { ["codigo"] = "000000" }))
            .Content.ReadAsStringAsync()); // código errado: erro na tela, nada ativado
        var ativado = await (await nav.PostFormAsync("/Conta/AtivarDoisFatores", "/Conta/DoisFatores", new() { ["codigo"] = Totp.Codigo(chave) })).Content.ReadAsStringAsync();
        var recuperacao = Regex.Matches(ativado, "<div class=\"col-6 col-md-4\"><code>([^<]+)</code></div>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, recuperacao.Count);

        // Login agora pede a segunda etapa; a senha sozinha não autentica
        var login = Novo();
        var r = await login.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Senha"] = AppMultiTenant.Senha });
        Assert.Contains("/Account/LoginDoisFatores", r.Headers.Location!.OriginalString);
        Assert.False(Autenticou(r));
        Assert.Equal(HttpStatusCode.Redirect, (await login.GetAsync("/Tarefas")).StatusCode);

        var errado = await login.PostFormAsync("/Account/LoginDoisFatores", r.Headers.Location.OriginalString, new() { ["codigo"] = "123456" });
        Assert.Equal(HttpStatusCode.OK, errado.StatusCode);
        Assert.True(Autenticou(await login.PostFormAsync("/Account/LoginDoisFatores", r.Headers.Location.OriginalString, new() { ["codigo"] = Totp.Codigo(chave) })));
        Assert.Equal(HttpStatusCode.OK, (await login.GetAsync("/Tarefas")).StatusCode);

        // Código de recuperação funciona uma única vez
        async Task<bool> EntrarComRecuperacao(string codigo)
        {
            var n = Novo();
            var p = await n.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Senha"] = AppMultiTenant.Senha });
            return Autenticou(await n.PostFormAsync("/Account/LoginDoisFatores", p.Headers.Location!.OriginalString,
                new() { ["codigo"] = codigo, ["usarRecuperacao"] = "true" }));
        }
        Assert.True(await EntrarComRecuperacao(recuperacao[0]));
        Assert.False(await EntrarComRecuperacao(recuperacao[0]));

        // Desativar exige senha + código
        await nav.PostFormAsync("/Conta/DesativarDoisFatores", "/Conta/DoisFatores", new() { ["senha"] = "senha-errada", ["codigo"] = Totp.Codigo(chave) });
        using (var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            Assert.True((await escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>().FindByEmailAsync(email))!.TwoFactorEnabled);
        await nav.PostFormAsync("/Conta/DesativarDoisFatores", "/Conta/DoisFatores", new() { ["senha"] = AppMultiTenant.Senha, ["codigo"] = recuperacao[1] });
        using (var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
            Assert.False((await escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>().FindByEmailAsync(email))!.TwoFactorEnabled);

        using var db = app.BancoSistema(out var e);
        using (e)
        {
            var uid = (await db.Users.SingleAsync(u => u.Email == email)).Id;
            var acoes = await db.RegistrosAuditoria.Where(a => a.UsuarioId == uid || a.EntidadeId == uid).Select(a => a.Acao).ToListAsync();
            Assert.Contains("conta.2fa-ativado", acoes);
            Assert.Contains("conta.login-2fa", acoes);
            Assert.Contains("conta.login-2fa-recuperacao", acoes);
            Assert.Contains("conta.2fa-desativado", acoes);
        }
    }

    [Fact]
    public async Task Superadmin_sem_2FA_e_levado_a_ativar_antes_do_painel()
    {
        using var exige = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["SUPERADMIN_EXIGIR_2FA"] = "true" })));
        var nav = new Navegador(exige.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        await nav.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = AppMultiTenant.SuperEmail, ["Senha"] = AppMultiTenant.Senha });
        var r = await nav.GetAsync("/Plataforma");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Conta/DoisFatores", r.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/Conta/DoisFatores")).StatusCode);
    }

    [Fact]
    public async Task Health_checks_e_pagina_publica_de_status_sem_detalhes_internos()
    {
        var anonimo = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonimo.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonimo.GetAsync("/health/live")).StatusCode);
        var ready = await anonimo.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());

        var status = await (await anonimo.GetAsync("/status")).Content.ReadAsStringAsync();
        Assert.Contains("Banco de dados", status);
        Assert.Contains("Operacional", status);
        var json = await (await anonimo.GetAsync("/status.json")).Content.ReadAsStringAsync();
        foreach (var segredo in new[] { "Host=", "Password", "agenciaos-local", "Exception" })
        {
            Assert.DoesNotContain(segredo, status.Replace("Formato para monitores", ""));
            Assert.DoesNotContain(segredo, json);
        }

        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        var saude = await super.GetAsync("/Plataforma/Saude");
        Assert.Equal(HttpStatusCode.OK, saude.StatusCode);
        Assert.Contains("banco", await saude.Content.ReadAsStringAsync());
        var alpha = await app.LoginAsync("admin@alpha.test");
        Assert.Contains("AcessoNegado", (await alpha.GetAsync("/Plataforma/Saude")).Headers.Location!.OriginalString);
    }
}
