using System.IO.Compression;
using System.Net;
using System.Text;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Plataforma;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgenciaOS.Tests;

public class TestesMetricasPlataforma
{
    [Fact]
    public async Task Mrr_arr_e_churn_consideram_periodicidade_e_cancelamentos()
    {
        using var banco = new BancoTeste();
        using (var db = banco.ContextoSistema())
        {
            var mensal = new Plano { Codigo = "m", Nome = "M", Preco = 100m, Periodicidade = Periodicidade.Mensal };
            var anual = new Plano { Codigo = "a", Nome = "A", Preco = 1200m, Periodicidade = Periodicidade.Anual };
            db.Planos.AddRange(mensal, anual);
            db.Agencias.Add(new Agencia { Id = 3, Nome = "Gamma", Slug = "gamma", CriadoEm = DateTime.UtcNow.AddDays(-100) });
            await db.SaveChangesAsync();
            db.Assinaturas.AddRange(
                new Assinatura { AgenciaId = 1, PlanoId = mensal.Id, Valor = 100m, Situacao = SituacaoAssinatura.Ativa, Gateway = "t", GatewayAssinaturaId = "s1" },
                new Assinatura { AgenciaId = 2, PlanoId = anual.Id, Valor = 1200m, Situacao = SituacaoAssinatura.Tolerancia, Gateway = "t", GatewayAssinaturaId = "s2" },
                new Assinatura { AgenciaId = 3, PlanoId = mensal.Id, Valor = 100m, Situacao = SituacaoAssinatura.Ativa, Gateway = "t", GatewayAssinaturaId = "s3",
                    CanceladaEm = DateTime.UtcNow.AddDays(-3) });
            await db.SaveChangesAsync();
        }

        using var sis = banco.ContextoSistema();
        var i = await new MetricasPlataformaService(sis).CalcularAsync(DateTime.UtcNow);
        Assert.Equal(200m, i.Mrr);            // 100 + 1200/12; a cancelada não entra
        Assert.Equal(2400m, i.Arr);
        Assert.Equal(1, i.CanceladasNoPeriodo);
        Assert.Equal(33.3m, i.TaxaCancelamento); // 1 / (2 pagantes + 1 cancelada)
        Assert.Equal(2, i.NovasNoPeriodo);     // Alpha e Beta criadas agora; Gamma há 100 dias
    }
}

/// <summary>Fase 5: painel global, auditoria, isenção, LGPD (exportação, anonimização, pedidos de titulares, novo aceite).</summary>
[Collection("http")]
[Trait("Categoria", "Integracao")]
public class TestesHttpPlataforma(AppMultiTenant app)
{
    private record Ag(int Id, string Admin, string Equipe, int ClienteId);

    private async Task<Ag> CriarAgenciaAsync(bool isenta = false, SituacaoAssinatura situacao = SituacaoAssinatura.Ativa, DateTime? testeAte = null)
    {
        var sufixo = Guid.NewGuid().ToString("N")[..8];
        using var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema();
        var db = escopo.ServiceProvider.GetRequiredService<Data.ApplicationDbContext>();
        var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var ag = new Agencia { Nome = $"Ag {sufixo}", Slug = $"pl-{sufixo}", Isenta = isenta, SituacaoAssinatura = situacao, TesteExpiraEm = testeAte };
        db.Agencias.Add(ag);
        await db.SaveChangesAsync();
        var cli = new Cliente { AgenciaId = ag.Id, NomeEmpresa = $"Cliente Sigiloso {sufixo}", Responsavel = "Fulano de Tal", Email = $"fulano-{sufixo}@cliente.test", Telefone = "11999999999" };
        db.Clientes.Add(cli);
        await db.SaveChangesAsync();
        db.Financeiros.Add(new Financeiro { AgenciaId = ag.Id, ClienteId = cli.Id, Descricao = "Mensalidade", Valor = 1000m, DataVencimento = DateTime.UtcNow });
        await db.SaveChangesAsync();

        async Task<string> U(string tipo, TipoUsuario t, string role)
        {
            var email = $"{tipo}-{sufixo}@pl.test";
            var u = new Usuario { UserName = email, Email = email, Nome = tipo, AgenciaId = ag.Id, Tipo = t, EmailConfirmed = true };
            Assert.True((await um.CreateAsync(u, AppMultiTenant.Senha)).Succeeded);
            await um.AddToRoleAsync(u, role);
            return email;
        }
        return new Ag(ag.Id, await U("admin", TipoUsuario.Admin, "Admin"), await U("equipe", TipoUsuario.Equipe, "Equipe"), cli.Id);
    }

    private EnviadorEmailMemoria Emails => app.Services.GetRequiredService<EnviadorEmailMemoria>();

    private static bool Negado(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
        || (r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location?.OriginalString.Contains("AcessoNegado") == true);

    [Fact]
    public async Task Agencia_isenta_nunca_e_bloqueada_por_cobranca_nem_pela_rotina()
    {
        var ag = await CriarAgenciaAsync(isenta: true, situacao: SituacaoAssinatura.Teste, testeAte: DateTime.UtcNow.AddDays(-30));
        var admin = await app.LoginAsync(ag.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Clientes")).StatusCode);

        using (var escopo = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema())
            await escopo.ServiceProvider.GetRequiredService<CobrancaService>().AtualizarSituacoesAsync(DateTime.UtcNow);
        using var db = app.BancoSistema(out var e);
        using (e) Assert.NotEqual(SituacaoAssinatura.Suspensa, (await db.Agencias.SingleAsync(a => a.Id == ag.Id)).SituacaoAssinatura);
    }

    [Fact]
    public async Task Superadmin_suspende_e_reativa_agencia_com_auditoria()
    {
        var ag = await CriarAgenciaAsync();
        var admin = await app.LoginAsync(ag.Admin);
        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);

        Assert.Equal(HttpStatusCode.Redirect, (await super.PostFormAsync($"/Plataforma/Suspender/{ag.Id}", $"/Plataforma/Agencia/{ag.Id}", new() { ["motivo"] = "Uso indevido" })).StatusCode);

        // Sessão aberta cai e novo login é recusado; dados intactos
        var r = await admin.GetAsync("/Clientes");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString);
        var nav = new Navegador(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
        var login = await nav.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = ag.Admin, ["Senha"] = AppMultiTenant.Senha });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await super.PostFormAsync($"/Plataforma/Reativar/{ag.Id}", $"/Plataforma/Agencia/{ag.Id}", new())).StatusCode);
        var denovo = await app.LoginAsync(ag.Admin);
        Assert.Equal(HttpStatusCode.OK, (await denovo.GetAsync($"/Clientes/Details/{ag.ClienteId}")).StatusCode);

        using var db = app.BancoSistema(out var e);
        using (e)
        {
            var acoes = await db.RegistrosAuditoria.Where(x => x.AgenciaId == ag.Id).Select(x => x.Acao).ToListAsync();
            Assert.Contains("plataforma.suspender-agencia", acoes);
            Assert.Contains("plataforma.reativar-agencia", acoes);
            Assert.Contains("conta.login", acoes);
            var reg = await db.RegistrosAuditoria.FirstAsync(x => x.AgenciaId == ag.Id && x.Acao == "plataforma.suspender-agencia");
            Assert.Equal(AppMultiTenant.SuperEmail, reg.UsuarioEmail);
            Assert.Contains("Uso indevido", reg.Detalhes);
        }

        // Agência não vê nada do painel global
        Assert.True(Negado(await denovo.GetAsync("/Plataforma/Auditoria")));
        Assert.True(Negado(await denovo.GetAsync($"/Plataforma/Agencia/{ag.Id}")));
    }

    [Fact]
    public async Task Painel_global_mostra_indicadores_e_detalhes()
    {
        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        var html = await (await super.GetAsync("/Plataforma")).Content.ReadAsStringAsync();
        Assert.Contains("(MRR)", html);
        Assert.Contains("(ARR)", html);
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync($"/Plataforma/Agencia/{app.AgenciaBeta}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync("/Plataforma/Auditoria")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync("/Plataforma/Configuracoes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await super.GetAsync("/Plataforma/Privacidade")).StatusCode);
    }

    [Fact]
    public async Task Exportacao_da_agencia_tem_somente_os_dados_dela_e_sem_segredos()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        var r = await alpha.PostFormAsync("/Dados/Exportar", "/Dados", new());
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);

        using var zip = new ZipArchive(new MemoryStream(await r.Content.ReadAsByteArrayAsync()));
        string Ler(string nome) { using var s = new StreamReader(zip.GetEntry(nome)!.Open()); return s.ReadToEnd(); }
        var clientes = Ler("clientes.json");
        Assert.Contains("Padaria Alpha", clientes);
        Assert.DoesNotContain("Clinica Beta Confidencial", clientes);
        var usuarios = Ler("usuarios.json");
        Assert.Contains("admin@alpha.test", usuarios);
        Assert.DoesNotContain("admin@beta.test", usuarios);
        Assert.DoesNotContain("PasswordHash", usuarios);
        Assert.DoesNotContain("Tarefa original Beta", Ler("tarefas.json"));

        // Só Admin exporta
        var ag = await CriarAgenciaAsync();
        var equipe = await app.LoginAsync(ag.Equipe);
        Assert.True(Negado(await equipe.PostFormAsync("/Dados/Exportar", "/Tarefas", new())));
    }

    [Fact]
    public async Task Titular_baixa_os_proprios_dados_sem_segredos()
    {
        var ag = await CriarAgenciaAsync();
        var nav = await app.LoginAsync(ag.Equipe);
        var r = await nav.PostFormAsync("/Conta/ExportarMeusDados", "/Conta/Privacidade", new());
        Assert.Equal("application/json", r.Content.Headers.ContentType?.MediaType);
        var json = await r.Content.ReadAsStringAsync();
        Assert.Contains(ag.Equipe, json);
        Assert.DoesNotContain("PasswordHash", json);
        Assert.DoesNotContain("SecurityStamp", json);
    }

    [Fact]
    public async Task Pedido_publico_de_titular_e_encaminhado_somente_a_agencia_certa()
    {
        var email = $"titular-{Guid.NewGuid():N}@pessoa.test";
        var anonimo = new Navegador(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }));
        var r = await anonimo.PostFormAsync("/Privacidade/Solicitar", "/Privacidade/Solicitar", new()
        {
            ["nome"] = "Pessoa Titular", ["email"] = email, ["tipo"] = "Exclusao", ["descricao"] = "Sou cliente da Beta e quero excluir meus dados."
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains(Emails.Enviadas, m => m.Para == email && m.Assunto.Contains("LGPD-"));

        SolicitacaoPrivacidade s;
        using (var db = app.BancoSistema(out var e))
        using (e) s = await db.SolicitacoesPrivacidade.SingleAsync(x => x.EmailTitular == email);
        Assert.Null(s.AgenciaId);
        Assert.InRange(s.PrazoEm, DateTime.UtcNow.AddDays(14), DateTime.UtcNow.AddDays(16));

        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        await super.PostFormAsync("/Plataforma/ResponderPrivacidade", "/Plataforma/Privacidade", new()
        {
            ["id"] = s.Id.ToString(), ["status"] = "EncaminhadaAgencia", ["agenciaId"] = app.AgenciaBeta.ToString()
        });

        var beta = await app.LoginAsync("admin@beta.test");
        Assert.Contains(s.Protocolo, await (await beta.GetAsync("/Privacidade")).Content.ReadAsStringAsync());
        var alpha = await app.LoginAsync("admin@alpha.test");
        Assert.DoesNotContain(s.Protocolo, await (await alpha.GetAsync("/Privacidade")).Content.ReadAsStringAsync());
        // Alpha não consegue responder pedido da Beta
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.PostFormAsync("/Privacidade/Responder", "/Privacidade", new()
            { ["id"] = s.Id.ToString(), ["status"] = "Concluida", ["resposta"] = "x" })).StatusCode);

        await beta.PostFormAsync("/Privacidade/Responder", "/Privacidade", new()
            { ["id"] = s.Id.ToString(), ["status"] = "Concluida", ["resposta"] = "Dados excluídos." });
        Assert.Contains(Emails.Enviadas, m => m.Para == email && m.Texto.Contains("Dados excluídos."));
        using var db2 = app.BancoSistema(out var e2);
        using (e2) Assert.Equal(StatusSolicitacaoPrivacidade.Concluida, (await db2.SolicitacoesPrivacidade.SingleAsync(x => x.Id == s.Id)).Status);
    }

    [Fact]
    public async Task Anonimizacao_de_titular_encerra_acesso_e_preserva_aceites()
    {
        var ag = await CriarAgenciaAsync();
        var nav = await app.LoginAsync(ag.Equipe);
        await nav.PostFormAsync("/Conta/AceitarTermos", "/Conta/Privacidade", new() { ["aceita"] = "true" });
        await nav.PostFormAsync("/Conta/SolicitarPrivacidade", "/Conta/Privacidade", new() { ["tipo"] = "Exclusao", ["descricao"] = "Quero sair." });

        int id; string uid;
        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            var u = await db.Users.SingleAsync(x => x.Email == ag.Equipe);
            uid = u.Id;
            id = (await db.SolicitacoesPrivacidade.SingleAsync(x => x.UsuarioId == uid)).Id;
        }

        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        await super.PostFormAsync("/Plataforma/AnonimizarTitular", "/Plataforma/Privacidade", new() { ["id"] = id.ToString(), ["confirmacao"] = "EXCLUIR" });

        using (var db = app.BancoSistema(out var e))
        using (e)
        {
            var u = await db.Users.SingleAsync(x => x.Id == uid);
            Assert.EndsWith("@anonimizado.invalid", u.Email);
            Assert.Equal("Titular removido", u.Nome);
            Assert.False(u.Ativo);
            Assert.Null(u.PasswordHash);
            Assert.True(await db.ConsentimentosTermos.AnyAsync(c => c.UsuarioId == uid)); // prova legal mantida
        }
        var r = await nav.GetAsync("/Tarefas");
        Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString);
        var login = await new Navegador(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
            .PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = ag.Equipe, ["Senha"] = AppMultiTenant.Senha });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); // não autentica
    }

    [Fact]
    public async Task Agencia_exclui_dados_de_um_cliente_mantendo_o_financeiro_e_nao_mexe_em_outra_agencia()
    {
        var ag = await CriarAgenciaAsync();
        var admin = await app.LoginAsync(ag.Admin);

        // Tentar anonimizar cliente de outra agência
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostFormAsync("/Dados/AnonimizarCliente", "/Dados", new()
            { ["id"] = app.ClienteBeta.ToString(), ["confirmacao"] = "EXCLUIR" })).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostFormAsync("/Dados/AnonimizarCliente", "/Dados", new()
            { ["id"] = ag.ClienteId.ToString(), ["confirmacao"] = "EXCLUIR" })).StatusCode);

        using var db = app.BancoSistema(out var e);
        using (e)
        {
            var c = await db.Clientes.SingleAsync(x => x.Id == ag.ClienteId);
            Assert.StartsWith("Cliente removido", c.NomeEmpresa);
            Assert.Null(c.Telefone);
            Assert.Equal("", c.Email);
            Assert.True(await db.Financeiros.AnyAsync(f => f.ClienteId == ag.ClienteId && f.Valor == 1000m));
            Assert.Equal("Clinica Beta Confidencial", (await db.Clientes.SingleAsync(x => x.Id == app.ClienteBeta)).NomeEmpresa);
            Assert.Contains(await db.RegistrosAuditoria.Where(r => r.AgenciaId == ag.Id).Select(r => r.Acao).ToListAsync(), a => a == "privacidade.anonimizar-cliente");
        }
    }

    [Fact]
    public async Task Novo_aceite_e_exigido_quando_os_termos_mudam()
    {
        using var comAceite = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["LGPD_EXIGIR_ACEITE"] = "true" })));
        var ag = await CriarAgenciaAsync();
        var nav = new Navegador(comAceite.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        await nav.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = ag.Equipe, ["Senha"] = AppMultiTenant.Senha });

        var r = await nav.GetAsync("/Tarefas");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Conta/AceitarTermos", r.Headers.Location!.OriginalString);

        // Sem marcar o aceite continua bloqueado
        Assert.Equal(HttpStatusCode.OK, (await nav.PostFormAsync("/Conta/AceitarTermos", "/Conta/AceitarTermos", new())).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await nav.PostFormAsync("/Conta/AceitarTermos", "/Conta/AceitarTermos", new() { ["aceita"] = "true", ["returnUrl"] = "/Tarefas" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nav.GetAsync("/Tarefas")).StatusCode);

        using var db = app.BancoSistema(out var e);
        using (e)
        {
            var uid = (await db.Users.SingleAsync(u => u.Email == ag.Equipe)).Id;
            Assert.True(await db.ConsentimentosTermos.AnyAsync(c => c.UsuarioId == uid && c.Documento == DocumentosLegais.Termos && c.Versao == DocumentosLegais.VersaoTermos));
        }
    }

    [Fact]
    public async Task Configuracao_global_define_dias_de_teste_do_cadastro_publico()
    {
        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        async Task Salvar(int dias) => await super.PostFormAsync("/Plataforma/Configuracoes", "/Plataforma/Configuracoes", new()
            { ["DiasTeste"] = dias.ToString(), ["DiasTolerancia"] = "7", ["ExigirAceiteTermos"] = "true" });
        try
        {
            await Salvar(30);
            var email = $"dias-{Guid.NewGuid():N}@teste.test";
            await new Navegador(app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
                .PostFormAsync("/Account/Cadastro", "/Account/Cadastro", new()
                {
                    ["NomeAgencia"] = "Agência 30 dias", ["NomeAdmin"] = "X", ["Email"] = email,
                    ["Senha"] = AppMultiTenant.Senha, ["ConfirmarSenha"] = AppMultiTenant.Senha, ["AceitaTermos"] = "true"
                });
            using var db = app.BancoSistema(out var e);
            using (e)
            {
                var agId = (await db.Users.SingleAsync(u => u.Email == email)).AgenciaId;
                var fim = (await db.Agencias.SingleAsync(a => a.Id == agId)).TesteExpiraEm!.Value;
                Assert.InRange(fim, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
                Assert.Contains(await db.RegistrosAuditoria.Select(r => r.Acao).ToListAsync(), a => a == "plataforma.configuracoes");
            }
        }
        finally { await Salvar(14); }
    }
}
