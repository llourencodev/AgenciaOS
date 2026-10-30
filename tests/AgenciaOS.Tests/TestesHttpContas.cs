using System.Net;
using System.Text.RegularExpressions;
using AgenciaOS.Models;
using AgenciaOS.Services.Contas;
using AgenciaOS.Services.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>Fase 3: cadastro público, confirmação de e-mail, recuperação de senha, convites, sessões e rate limiting.</summary>
[Collection("http")]
[Trait("Categoria", "Integracao")]
public class TestesHttpContas(AppMultiTenant app)
{
    private const string SenhaNova = "Nova-senha-456X";

    private EnviadorEmailMemoria Emails => app.Services.GetRequiredService<EnviadorEmailMemoria>();

    private static string Unico(string prefixo) => $"{prefixo}-{Guid.NewGuid():N}"[..(prefixo.Length + 9)] + "@teste.test";

    private MensagemEmail UltimoEmailPara(string email) =>
        Emails.Enviadas.LastOrDefault(m => m.Para == email) ?? throw new Xunit.Sdk.XunitException($"Nenhum e-mail para {email}");

    private static string LinkDo(MensagemEmail m)
    {
        var url = Regex.Match(m.Texto, @"https?://\S+").Value;
        return new Uri(url).PathAndQuery;
    }

    private Navegador NovoNavegador() =>
        new(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    private static Dictionary<string, string> FormCadastro(string email, string agencia = "Agência Delta") => new()
    {
        ["NomeAgencia"] = agencia, ["NomeAdmin"] = "Dona Delta", ["Email"] = email,
        ["Senha"] = AppMultiTenant.Senha, ["ConfirmarSenha"] = AppMultiTenant.Senha, ["AceitaTermos"] = "true"
    };

    private static async Task<HttpResponseMessage> Login(Navegador nav, string email, string senha) =>
        await nav.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Senha"] = senha });

    private static bool Autenticou(HttpResponseMessage r) =>
        r.StatusCode == HttpStatusCode.Redirect && r.Headers.TryGetValues("Set-Cookie", out var c) && c.Any(x => x.StartsWith(".AspNetCore.Identity.Application="));

    [Fact]
    public async Task Cadastro_publico_cria_agencia_isolada_e_exige_confirmacao_de_email()
    {
        var email = Unico("dona");
        var nav = NovoNavegador();

        var r = await nav.PostFormAsync("/Account/Cadastro", "/Account/Cadastro", FormCadastro(email));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.EndsWith("/Account/VerifiqueEmail", r.Headers.Location!.OriginalString);

        int agenciaId;
        using (var db = app.BancoSistema(out var escopo))
        using (escopo)
        {
            var u = await db.Users.SingleAsync(x => x.Email == email);
            Assert.False(u.EmailConfirmed);
            Assert.NotNull(u.AgenciaId);
            agenciaId = u.AgenciaId!.Value;
            var ag = await db.Agencias.SingleAsync(a => a.Id == agenciaId);
            Assert.Equal(SituacaoAssinatura.Teste, ag.SituacaoAssinatura);
            Assert.InRange(ag.TesteExpiraEm!.Value, DateTime.UtcNow.AddDays(ContasService.DiasTeste - 1), DateTime.UtcNow.AddDays(ContasService.DiasTeste + 1));
            Assert.True(await db.Configuracoes.AnyAsync(c => c.AgenciaId == agenciaId));
            var aceites = await db.ConsentimentosTermos.Where(c => c.UsuarioId == u.Id).ToListAsync();
            Assert.Contains(aceites, c => c.Documento == DocumentosLegais.Termos && c.Versao == DocumentosLegais.VersaoTermos);
            Assert.Contains(aceites, c => c.Documento == DocumentosLegais.Privacidade && c.Versao == DocumentosLegais.VersaoPrivacidade);
            Assert.All(aceites, c => Assert.Equal(agenciaId, c.AgenciaId));
        }

        // Antes de confirmar: senha certa, mas não entra
        Assert.False(Autenticou(await Login(NovoNavegador(), email, AppMultiTenant.Senha)));

        var confirmar = await nav.GetAsync(LinkDo(UltimoEmailPara(email)));
        Assert.Contains("E-mail confirmado", await confirmar.Content.ReadAsStringAsync());

        var logado = NovoNavegador();
        Assert.True(Autenticou(await Login(logado, email, AppMultiTenant.Senha)));
        var clientes = await (await logado.GetAsync("/Clientes")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Padaria Alpha", clientes);
        Assert.DoesNotContain("Clinica Beta Confidencial", clientes);
        Assert.Equal(HttpStatusCode.NotFound, (await logado.GetAsync($"/Clientes/Details/{app.ClienteA}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await logado.GetAsync("/Usuarios")).StatusCode); // é Admin da própria agência
    }

    [Fact]
    public async Task Cadastro_publico_nunca_cria_superadmin_nem_entra_em_agencia_existente()
    {
        var email = Unico("intruso");
        var form = FormCadastro(email);
        form["Tipo"] = "SuperAdmin";
        form["Role"] = "SuperAdmin";
        form["AgenciaId"] = app.AgenciaAlpha.ToString();
        await NovoNavegador().PostFormAsync("/Account/Cadastro", "/Account/Cadastro", form);

        using var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var u = await um.FindByEmailAsync(email);
        Assert.NotNull(u);
        Assert.Equal(["Admin"], await um.GetRolesAsync(u!));
        Assert.NotEqual(app.AgenciaAlpha, u!.AgenciaId);
        Assert.NotNull(u.AgenciaId);
    }

    [Fact]
    public async Task Cadastro_com_email_existente_nao_revela_nem_cria_nada()
    {
        int antes;
        using (var db = app.BancoSistema(out var e1)) using (e1) antes = await db.Agencias.CountAsync();

        var r = await NovoNavegador().PostFormAsync("/Account/Cadastro", "/Account/Cadastro", FormCadastro("admin@alpha.test", "Agência Clone"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); // mesma resposta de um cadastro novo
        Assert.Contains("já tem uma conta", UltimoEmailPara("admin@alpha.test").Texto);

        using var db2 = app.BancoSistema(out var e2);
        using (e2) Assert.Equal(antes, await db2.Agencias.CountAsync());
    }

    [Fact]
    public async Task Cadastro_recusa_robo_sem_aceite_e_senha_fraca()
    {
        var robo = Unico("robo");
        var form = FormCadastro(robo);
        form["Website"] = "http://spam.example";
        Assert.Equal(HttpStatusCode.Redirect, (await NovoNavegador().PostFormAsync("/Account/Cadastro", "/Account/Cadastro", form)).StatusCode);

        var semAceite = Unico("semaceite");
        var form2 = FormCadastro(semAceite);
        form2.Remove("AceitaTermos");
        var r2 = await NovoNavegador().PostFormAsync("/Account/Cadastro", "/Account/Cadastro", form2);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var fraca = Unico("fraca");
        var form3 = FormCadastro(fraca);
        form3["Senha"] = form3["ConfirmarSenha"] = "12345678";
        var r3 = await NovoNavegador().PostFormAsync("/Account/Cadastro", "/Account/Cadastro", form3);
        Assert.Contains("pelo menos 10 caracteres", await r3.Content.ReadAsStringAsync());

        using var db = app.BancoSistema(out var escopo);
        using (escopo)
            Assert.False(await db.Users.AnyAsync(u => u.Email == robo || u.Email == semAceite || u.Email == fraca));
    }

    [Fact]
    public async Task Recuperacao_de_senha_troca_a_senha_derruba_sessoes_e_o_link_vale_uma_vez()
    {
        var email = Unico("esquecido");
        using (var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            var u = new Usuario { UserName = email, Email = email, Nome = "Esquecido", AgenciaId = app.AgenciaAlpha, EmailConfirmed = true, Tipo = TipoUsuario.Equipe };
            Assert.True((await um.CreateAsync(u, AppMultiTenant.Senha)).Succeeded);
            await um.AddToRoleAsync(u, "Equipe");
        }

        var sessaoAntiga = NovoNavegador();
        Assert.True(Autenticou(await Login(sessaoAntiga, email, AppMultiTenant.Senha)));

        // E-mail inexistente: mesma resposta, nenhum envio
        var antes = Emails.Enviadas.Count;
        Assert.Equal(HttpStatusCode.Redirect, (await NovoNavegador().PostFormAsync("/Account/EsqueciSenha", "/Account/EsqueciSenha", new() { ["email"] = "ninguem@teste.test" })).StatusCode);
        Assert.Equal(antes, Emails.Enviadas.Count);

        var nav = NovoNavegador();
        await nav.PostFormAsync("/Account/EsqueciSenha", "/Account/EsqueciSenha", new() { ["email"] = email });
        var link = LinkDo(UltimoEmailPara(email));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://x" + link).Query);
        var campos = new Dictionary<string, string> { ["email"] = email, ["code"] = query["code"]!, ["senha"] = SenhaNova, ["confirmarSenha"] = SenhaNova };

        // Token adulterado não serve
        var adulterado = new Dictionary<string, string>(campos) { ["code"] = query["code"] + "x" };
        Assert.Equal(HttpStatusCode.OK, (await nav.PostFormAsync("/Account/RedefinirSenha", link, adulterado)).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await nav.PostFormAsync("/Account/RedefinirSenha", link, campos)).StatusCode);
        Assert.False(Autenticou(await Login(NovoNavegador(), email, AppMultiTenant.Senha)));
        Assert.True(Autenticou(await Login(NovoNavegador(), email, SenhaNova)));

        // A sessão aberta antes da troca caiu
        var r = await sessaoAntiga.GetAsync("/Tarefas");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString);

        // O mesmo link não funciona de novo
        var reuso = new Dictionary<string, string>(campos) { ["senha"] = "Outra-senha-789Z", ["confirmarSenha"] = "Outra-senha-789Z" };
        Assert.Equal(HttpStatusCode.OK, (await nav.PostFormAsync("/Account/RedefinirSenha", link, reuso)).StatusCode);
    }

    [Fact]
    public async Task Convite_cria_usuario_na_agencia_de_quem_convidou_e_so_vale_uma_vez()
    {
        var convidado = Unico("convidado");
        var alpha = await app.LoginAsync("admin@alpha.test");
        var r = await alpha.PostFormAsync("/Usuarios/Convidar", "/Usuarios", new()
        {
            ["email"] = convidado, ["nome"] = "Convidado", ["tipo"] = "ColaboradorExterno", ["abasPermitidas"] = "Tarefas"
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var link = LinkDo(UltimoEmailPara(convidado));
        var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://x" + link).Query)["token"].ToString();

        int conviteId;
        using (var db = app.BancoSistema(out var e1))
        using (e1)
        {
            var c = await db.ConvitesUsuario.SingleAsync(x => x.Email == convidado);
            Assert.Equal(app.AgenciaAlpha, c.AgenciaId);
            Assert.NotEqual(token, c.TokenHash); // só o hash fica no banco
            Assert.Equal(ContasService.HashToken(token), c.TokenHash);
            conviteId = c.Id;
        }

        // Admin da Beta não revoga convite da Alpha
        var beta = await app.LoginAsync("admin@beta.test");
        var revogar = await beta.PostFormAsync($"/Usuarios/RevogarConvite/{conviteId}", "/Usuarios", new());
        Assert.Equal(HttpStatusCode.NotFound, revogar.StatusCode);

        var nav = NovoNavegador();
        Assert.Contains(convidado, await (await nav.GetAsync(link)).Content.ReadAsStringAsync());
        var aceitar = new Dictionary<string, string>
        {
            ["token"] = token, ["nome"] = "Convidado da Silva", ["senha"] = AppMultiTenant.Senha,
            ["confirmarSenha"] = AppMultiTenant.Senha, ["aceitaTermos"] = "true"
        };
        Assert.Equal(HttpStatusCode.Redirect, (await nav.PostFormAsync("/Account/Convite", link, aceitar)).StatusCode);

        using (var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            var u = await um.FindByEmailAsync(convidado);
            Assert.Equal(app.AgenciaAlpha, u!.AgenciaId);
            Assert.Equal(TipoUsuario.ColaboradorExterno, u.Tipo);
            Assert.Equal("Tarefas", u.AbasPermitidas);
            Assert.Equal(["ColaboradorExterno"], await um.GetRolesAsync(u));
        }

        var logado = NovoNavegador();
        Assert.True(Autenticou(await Login(logado, convidado, AppMultiTenant.Senha)));
        Assert.True(Negado(await logado.GetAsync("/Clientes")));

        // Reuso do link
        var reuso = NovoNavegador();
        Assert.Contains("Convite inválido", await (await reuso.GetAsync(link)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Convite_expirado_ou_revogado_nao_funciona()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        var emailExp = Unico("expirado");
        var emailRev = Unico("revogado");
        await alpha.PostFormAsync("/Usuarios/Convidar", "/Usuarios", new() { ["email"] = emailExp, ["tipo"] = "Equipe" });
        await alpha.PostFormAsync("/Usuarios/Convidar", "/Usuarios", new() { ["email"] = emailRev, ["tipo"] = "Equipe" });
        var linkExp = LinkDo(UltimoEmailPara(emailExp));
        var linkRev = LinkDo(UltimoEmailPara(emailRev));

        int idRev;
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            var exp = await db.ConvitesUsuario.SingleAsync(c => c.Email == emailExp);
            exp.ExpiraEm = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            idRev = (await db.ConvitesUsuario.SingleAsync(c => c.Email == emailRev)).Id;
        }
        Assert.Equal(HttpStatusCode.Redirect, (await alpha.PostFormAsync($"/Usuarios/RevogarConvite/{idRev}", "/Usuarios", new())).StatusCode);

        Assert.Contains("Convite inválido", await (await NovoNavegador().GetAsync(linkExp)).Content.ReadAsStringAsync());
        Assert.Contains("Convite inválido", await (await NovoNavegador().GetAsync(linkRev)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Encerrar_outras_sessoes_derruba_os_demais_dispositivos()
    {
        var email = Unico("sessoes");
        using (var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            var u = new Usuario { UserName = email, Email = email, Nome = "Sessões", AgenciaId = app.AgenciaAlpha, EmailConfirmed = true, Tipo = TipoUsuario.Equipe };
            Assert.True((await um.CreateAsync(u, AppMultiTenant.Senha)).Succeeded);
            await um.AddToRoleAsync(u, "Equipe");
        }
        var celular = NovoNavegador();
        var computador = NovoNavegador();
        Assert.True(Autenticou(await Login(celular, email, AppMultiTenant.Senha)));
        Assert.True(Autenticou(await Login(computador, email, AppMultiTenant.Senha)));

        Assert.Equal(HttpStatusCode.Redirect, (await computador.PostFormAsync("/Conta/EncerrarOutrasSessoes", "/Conta/Seguranca", new())).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await computador.GetAsync("/Tarefas")).StatusCode);
        var r = await celular.GetAsync("/Tarefas");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Rate_limit_bloqueia_excesso_de_pedidos_por_ip()
    {
        using var restrita = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["RATE_LIMIT_CONTA"] = "2" })));
        var nav = new Navegador(restrita.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));

        var codigos = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            codigos.Add((await nav.PostFormAsync("/Account/EsqueciSenha", "/Account/Login", new() { ["email"] = "x@teste.test" })).StatusCode);

        Assert.Equal([HttpStatusCode.Redirect, HttpStatusCode.Redirect, HttpStatusCode.TooManyRequests], codigos);
    }

    private static bool Negado(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
        || (r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location?.OriginalString.Contains("AcessoNegado") == true);
}
