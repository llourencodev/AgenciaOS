using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenciaOS.Models;
using AgenciaOS.Services.Dominios;
using AgenciaOS.Services.Marketing;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>DNS controlado pelos testes (registros TXT).</summary>
public class DnsFalso : IConsultaDns
{
    public ConcurrentDictionary<string, string[]> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<IReadOnlyList<string>> TxtAsync(string nome, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Txt.TryGetValue(nome, out var v) ? v : []);
}

internal static class Assinaturas
{
    public static string SignedRequestMeta(string segredo, object payload)
    {
        var corpo = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var sig = WebEncoders.Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes(corpo)));
        return $"{sig}.{corpo}";
    }

    public static string CabecalhoTikTok(string segredo, string corpo, long t) =>
        $"t={t},s={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes($"{t}.{corpo}")))}";
}

public class TestesDominiosEPlataformas
{
    [Theory]
    [InlineData("https://Painel.MinhaAgencia.com.br/login?x=1", "painel.minhaagencia.com.br")]
    [InlineData("painel.minhaagencia.com.br.", "painel.minhaagencia.com.br")]
    [InlineData("painel.minhaagencia.com.br:8443", "painel.minhaagencia.com.br")]
    [InlineData("localhost", null)]
    [InlineData("user@evil.com", null)]
    [InlineData("-x.com", null)]
    [InlineData("a b.com", null)]
    [InlineData("", null)]
    public void Normaliza_e_valida_dominios(string entrada, string? esperado) =>
        Assert.Equal(esperado, DominiosService.Normalizar(entrada));

    [Fact]
    public void Dominio_com_acento_vira_punycode() =>
        Assert.Matches(@"^painel.xn--[a-z0-9-]+.com.br$", DominiosService.Normalizar("painel.agência.com.br"));

    private static ExclusaoDadosPlataformasService Servico(Data.ApplicationDbContext db, TenantContext t) =>
        new(db, null!, null!, new MarketingOptions { MetaAppSecret = "meta-s", InstagramAppSecret = "ig-s", TikTokClientSecret = "tt-s" }, t,
            NullLogger<ExclusaoDadosPlataformasService>.Instance);

    [Fact]
    public async Task Exclusao_Meta_so_aceita_assinatura_valida_e_apaga_so_o_usuario_indicado()
    {
        using var banco = new BancoTeste();
        int clienteA, clienteB;
        using (var db = banco.ContextoSistema())
        {
            var a = new Cliente { AgenciaId = 1, NomeEmpresa = "A", Responsavel = "r", Email = "e" };
            var b = new Cliente { AgenciaId = 2, NomeEmpresa = "B", Responsavel = "r", Email = "e" };
            db.Clientes.AddRange(a, b);
            await db.SaveChangesAsync();
            (clienteA, clienteB) = (a.Id, b.Id);
            db.ConexoesMarketing.AddRange(
                new ConexaoMarketing { AgenciaId = 1, ClienteId = a.Id, Plataforma = PlataformaMarketing.Instagram, UsuarioExternoId = "111", TokenAcessoProtegido = "x" },
                new ConexaoMarketing { AgenciaId = 1, ClienteId = a.Id, Plataforma = PlataformaMarketing.Facebook, UsuarioExternoId = "111", TokenAcessoProtegido = "x" },
                new ConexaoMarketing { AgenciaId = 2, ClienteId = b.Id, Plataforma = PlataformaMarketing.Instagram, UsuarioExternoId = "222", TokenAcessoProtegido = "x" },
                new ConexaoMarketing { AgenciaId = 1, ClienteId = a.Id, Plataforma = PlataformaMarketing.TikTok, UsuarioExternoId = "111", TokenAcessoProtegido = "x" });
            await db.SaveChangesAsync();
        }

        var t = new TenantContext();
        using (var db = banco.Contexto(t))
        {
            var s = Servico(db, t);
            Assert.Null(await s.ExclusaoMetaAsync(Assinaturas.SignedRequestMeta("segredo-errado", new { algorithm = "HMAC-SHA256", user_id = "111" })));
            Assert.Null(await s.ExclusaoMetaAsync("lixo"));
            Assert.Null(await s.ExclusaoMetaAsync(Assinaturas.SignedRequestMeta("meta-s", new { algorithm = "HMAC-SHA1", user_id = "111" })));

            var r = await s.ExclusaoMetaAsync(Assinaturas.SignedRequestMeta("ig-s", new { algorithm = "HMAC-SHA256", user_id = "111", issued_at = 1 }));
            Assert.NotNull(r);
            Assert.Equal(2, r!.Value.Conexoes);
            Assert.StartsWith("META-", r.Value.Codigo);
        }

        using var sis = banco.ContextoSistema();
        var restantes = await sis.ConexoesMarketing.Select(c => new { c.Plataforma, c.UsuarioExternoId }).ToListAsync();
        Assert.Equal(2, restantes.Count); // TikTok do mesmo id e o Instagram de outra pessoa ficam
        Assert.Contains(restantes, c => c.UsuarioExternoId == "222");
        Assert.Contains(restantes, c => c.Plataforma == PlataformaMarketing.TikTok);
        Assert.True(await sis.SolicitacoesPrivacidade.AnyAsync(x => x.Protocolo.StartsWith("META-") && x.Status == StatusSolicitacaoPrivacidade.Concluida));
    }

    [Fact]
    public async Task Webhook_TikTok_valida_assinatura_e_janela_de_tempo()
    {
        using var banco = new BancoTeste();
        using (var db = banco.ContextoSistema())
        {
            var c = new Cliente { AgenciaId = 1, NomeEmpresa = "A", Responsavel = "r", Email = "e" };
            db.Clientes.Add(c);
            await db.SaveChangesAsync();
            db.ConexoesMarketing.Add(new ConexaoMarketing { AgenciaId = 1, ClienteId = c.Id, Plataforma = PlataformaMarketing.TikTok, UsuarioExternoId = "open-1", TokenAcessoProtegido = "tok", Status = StatusIntegracao.Conectado });
            await db.SaveChangesAsync();
        }
        var corpo = "{\"client_key\":\"k\",\"event\":\"authorization.removed\",\"create_time\":1,\"user_openid\":\"open-1\",\"content\":\"{}\"}";
        var agora = DateTimeOffset.UtcNow;
        var t = new TenantContext();
        using (var db = banco.Contexto(t))
        {
            var s = Servico(db, t);
            Assert.True(s.AssinaturaTikTokValida(Assinaturas.CabecalhoTikTok("tt-s", corpo, agora.ToUnixTimeSeconds()), corpo, agora));
            Assert.False(s.AssinaturaTikTokValida(Assinaturas.CabecalhoTikTok("outro", corpo, agora.ToUnixTimeSeconds()), corpo, agora));
            Assert.False(s.AssinaturaTikTokValida(Assinaturas.CabecalhoTikTok("tt-s", corpo, agora.AddMinutes(-10).ToUnixTimeSeconds()), corpo, agora));
            Assert.False(s.AssinaturaTikTokValida(Assinaturas.CabecalhoTikTok("tt-s", corpo, agora.ToUnixTimeSeconds()), corpo + " ", agora));
            Assert.Equal(1, await s.EventoTikTokAsync(corpo));
        }
        using var sis = banco.ContextoSistema();
        var conexao = await sis.ConexoesMarketing.SingleAsync();
        Assert.Equal(StatusIntegracao.PermissaoRevogada, conexao.Status);
        Assert.Equal("", conexao.TokenAcessoProtegido);
    }
}

[Collection("http")]
[Trait("Categoria", "Integracao")]
public class TestesHttpFase7(AppMultiTenant app)
{
    private Navegador NoHost(string host, WebApplicationFactory<Program>? fabrica = null) =>
        new((fabrica ?? app).CreateClient(new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri($"http://{host}") }));

    private static bool Autenticou(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.Redirect && r.Headers.TryGetValues("Set-Cookie", out var c) && c.Any(x => x.StartsWith(".AspNetCore.Identity.Application="));

    [Fact]
    public async Task Dominio_proprio_verificado_por_DNS_mostra_a_marca_e_so_aceita_usuarios_da_agencia()
    {
        var host = $"painel-{Guid.NewGuid():N}"[..14] + ".alpha-teste.com.br";
        var alpha = await app.LoginAsync("admin@alpha.test");
        await alpha.PostFormAsync("/Dominios/Adicionar", "/Dominios", new() { ["host"] = $"https://{host.ToUpperInvariant()}/login" });

        DominioAgencia d;
        using (var db = app.BancoSistema(out var e))
        using (e) d = await db.DominiosAgencia.SingleAsync(x => x.Host == host);
        Assert.Equal(StatusDominio.Pendente, d.Status);
        Assert.Equal(app.AgenciaAlpha, d.AgenciaId);

        // Sem TXT: não verifica e o domínio ainda não funciona para a marca
        await alpha.PostFormAsync($"/Dominios/Verificar/{d.Id}", "/Dominios", new());
        Assert.DoesNotContain("Acesso exclusivo", await (await NoHost(host).GetAsync("/Account/Login")).Content.ReadAsStringAsync());

        // Outra agência não consegue cadastrar o mesmo domínio
        var beta = await app.LoginAsync("admin@beta.test");
        await beta.PostFormAsync("/Dominios/Adicionar", "/Dominios", new() { ["host"] = host });
        using (var db = app.BancoSistema(out var e))
        using (e) Assert.Equal(1, await db.DominiosAgencia.CountAsync(x => x.Host == host));
        // e não consegue verificar o domínio da Alpha
        Assert.Equal(HttpStatusCode.NotFound, (await beta.PostFormAsync($"/Dominios/Verificar/{d.Id}", "/Dominios", new())).StatusCode);

        app.Dns.Txt[d.NomeRegistroTxt] = [d.TokenVerificacao];
        await alpha.PostFormAsync($"/Dominios/Verificar/{d.Id}", "/Dominios", new());
        using (var db = app.BancoSistema(out var e))
        using (e) Assert.Equal(StatusDominio.Verificado, (await db.DominiosAgencia.SingleAsync(x => x.Id == d.Id)).Status);

        // Login no domínio: marca da agência; usuário de outra agência é recusado mesmo com a senha certa
        var navBeta = NoHost(host);
        Assert.Contains("Acesso exclusivo", await (await navBeta.GetAsync("/Account/Login")).Content.ReadAsStringAsync());
        Assert.False(Autenticou(await navBeta.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = "admin@beta.test", ["Senha"] = AppMultiTenant.Senha })));

        var navAlpha = NoHost(host);
        Assert.True(Autenticou(await navAlpha.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = "admin@alpha.test", ["Senha"] = AppMultiTenant.Senha })));
        Assert.Equal(HttpStatusCode.OK, (await navAlpha.GetAsync("/Clientes")).StatusCode);

        // O domínio não muda a agência dos dados: Alpha no próprio domínio continua sem ver a Beta
        Assert.Equal(HttpStatusCode.NotFound, (await navAlpha.GetAsync($"/Clientes/Details/{app.ClienteBeta}")).StatusCode);
    }

    [Fact]
    public async Task Host_desconhecido_recebe_404_quando_os_hosts_da_plataforma_estao_definidos()
    {
        using var restrita = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["PLATAFORMA_HOSTS"] = "app.agenciaos.test" })));
        Assert.Equal(HttpStatusCode.NotFound, (await NoHost("site-falso.example.com", restrita).GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NoHost("app.agenciaos.test", restrita).GetAsync("/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await NoHost("site-falso.example.com", restrita).GetAsync("/health")).StatusCode); // healthcheck do Railway
    }

    [Fact]
    public async Task Callback_de_exclusao_da_Meta_responde_no_formato_exigido()
    {
        var anon = app.CreateClient();
        var invalido = await anon.PostAsync("/integracoes/meta/exclusao-dados",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["signed_request"] = Assinaturas.SignedRequestMeta("chute", new { algorithm = "HMAC-SHA256", user_id = "9" }) }));
        Assert.Equal(HttpStatusCode.BadRequest, invalido.StatusCode);

        var r = await anon.PostAsync("/integracoes/meta/exclusao-dados",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["signed_request"] = Assinaturas.SignedRequestMeta(AppMultiTenant.SegredoMeta, new { algorithm = "HMAC-SHA256", user_id = "999999" }) }));
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        var codigo = json.GetProperty("confirmation_code").GetString()!;
        var url = new Uri(json.GetProperty("url").GetString()!);
        Assert.EndsWith(codigo, url.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync(url.PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/integracoes/exclusao/META-20260101-INEXIST")).StatusCode);
    }

    [Fact]
    public async Task Excluir_dados_de_uma_integracao_apaga_tudo_e_so_vale_para_a_propria_agencia()
    {
        int conexaoAlpha, conexaoBeta;
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            var ca = new ConexaoMarketing { AgenciaId = app.AgenciaAlpha, ClienteId = app.ClienteA, Plataforma = PlataformaMarketing.GoogleAnalytics, TokenAcessoProtegido = "", Status = StatusIntegracao.Conectado };
            var cb = new ConexaoMarketing { AgenciaId = app.AgenciaBeta, ClienteId = app.ClienteBeta, Plataforma = PlataformaMarketing.GoogleAnalytics, TokenAcessoProtegido = "", Status = StatusIntegracao.Conectado };
            db.ConexoesMarketing.AddRange(ca, cb);
            await db.SaveChangesAsync();
            db.ContasMarketing.Add(new ContaMarketing { AgenciaId = app.AgenciaAlpha, ClienteId = app.ClienteA, ConexaoMarketingId = ca.Id, Plataforma = PlataformaMarketing.GoogleAnalytics, IdExterno = $"ga-{ca.Id}", Nome = "GA" });
            await db.SaveChangesAsync();
            (conexaoAlpha, conexaoBeta) = (ca.Id, cb.Id);
        }

        var alpha = await app.LoginAsync("admin@alpha.test");
        var negado = await alpha.PostFormAsync("/Integracoes/ExcluirDados", $"/Integracoes?clienteId={app.ClienteA}", new() { ["conexaoId"] = conexaoBeta.ToString() });
        Assert.True(negado.StatusCode == HttpStatusCode.Forbidden || negado.Headers.Location?.OriginalString.Contains("AcessoNegado") == true);
        Assert.Equal(HttpStatusCode.Redirect, (await alpha.PostFormAsync("/Integracoes/ExcluirDados", $"/Integracoes?clienteId={app.ClienteA}", new() { ["conexaoId"] = conexaoAlpha.ToString() })).StatusCode);

        using var db2 = app.BancoSistema(out var e2);
        using (e2)
        {
            Assert.False(await db2.ConexoesMarketing.AnyAsync(c => c.Id == conexaoAlpha));
            Assert.False(await db2.ContasMarketing.AnyAsync(c => c.ConexaoMarketingId == conexaoAlpha));
            Assert.True(await db2.ConexoesMarketing.AnyAsync(c => c.Id == conexaoBeta));
        }
    }
}
