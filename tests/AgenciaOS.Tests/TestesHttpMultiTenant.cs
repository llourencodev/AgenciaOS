using System.Net;
using System.Text.RegularExpressions;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>
/// Sobe o AgenciaOS de verdade (migrations reais, Identity, cookies, antiforgery) contra um Postgres
/// de teste descartável. Conexão: variável AGENCIAOS_TEST_PG ou o Postgres local do Docker (porta 5434).
/// O banco <c>agenciaos_tests</c> é recriado a cada execução.
/// </summary>
public sealed class AppMultiTenant : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Senha = "Senha-de-teste-123";
    public const string SuperEmail = "super@plataforma.test";

    private static readonly string Base = Environment.GetEnvironmentVariable("AGENCIAOS_TEST_PG")
        ?? "Host=127.0.0.1;Port=5434;Username=agenciaos;Password=agenciaos-local";
    private static readonly string BancoTeste = Base + ";Database=agenciaos_tests";

    public int AgenciaAlpha, AgenciaBeta;
    public int ClienteA, ClienteA2, ClienteBeta, TarefaA, TarefaA2, TarefaBeta, ConteudoA2, ConteudoBeta;
    public string AdminBetaId = "";

    public AppMultiTenant()
    {
        // Lidas pelo Program.cs durante WebApplication.CreateBuilder
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", BancoTeste);
        Environment.SetEnvironmentVariable("MARKETING_SYNC_ENABLED", "false");
        Environment.SetEnvironmentVariable("SUPERADMIN_EMAIL", SuperEmail);
        Environment.SetEnvironmentVariable("SUPERADMIN_PASSWORD", Senha);
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_AGENCIAOS", RaizProjeto());
        Environment.SetEnvironmentVariable("EMAIL_PROVIDER", "memoria");
        Environment.SetEnvironmentVariable("SECURITY_STAMP_INTERVAL_SECONDS", "0"); // revogação de sessão imediata nos testes
        Environment.SetEnvironmentVariable("RATE_LIMIT_LOGIN", "100000"); // a suíte faz muitos logins do mesmo IP
        Environment.SetEnvironmentVariable("RATE_LIMIT_CONTA", "100000");
        Environment.SetEnvironmentVariable("STORAGE_PROVIDER", "local");
        Environment.SetEnvironmentVariable("STORAGE_LOCAL_PATH", Path.Combine(Path.GetTempPath(), "agenciaos-http-tests", Guid.NewGuid().ToString("N")));
    }

    private static string RaizProjeto()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgenciaOS.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgenciaOS.csproj não encontrado");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(RaizProjeto());
    }

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Base + ";Database=postgres"))
        {
            await conn.OpenAsync();
            await new NpgsqlCommand("DROP DATABASE IF EXISTS agenciaos_tests WITH (FORCE)", conn).ExecuteNonQueryAsync();
            await new NpgsqlCommand("CREATE DATABASE agenciaos_tests", conn).ExecuteNonQueryAsync();
        }

        _ = Server; // sobe a aplicação: aplica migrations e provisiona o superadmin

        using var escopo = Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema();
        var db = escopo.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var um = escopo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();

        // A migration criou a agência 1 (instalação existente) → vira a Alpha
        var alpha = await db.Agencias.SingleAsync();
        alpha.Nome = "Agência Alpha";
        var beta = new Agencia { Nome = "Agência Beta", Slug = "beta" };
        db.Agencias.Add(beta);
        await db.SaveChangesAsync();
        AgenciaAlpha = alpha.Id;
        AgenciaBeta = beta.Id;

        async Task<Usuario> Usuario(string email, int agencia, TipoUsuario tipo, string role, string? abas = null)
        {
            var u = new Usuario { UserName = email, Email = email, Nome = email.Split('@')[0], Tipo = tipo, AgenciaId = agencia, EmailConfirmed = true, AbasPermitidas = abas };
            var r = await um.CreateAsync(u, Senha);
            Assert.True(r.Succeeded, string.Join(";", r.Errors.Select(e => e.Description)));
            await um.AddToRoleAsync(u, role);
            return u;
        }

        await Usuario("admin@alpha.test", AgenciaAlpha, TipoUsuario.Admin, "Admin");
        var adminBeta = await Usuario("admin@beta.test", AgenciaBeta, TipoUsuario.Admin, "Admin");
        AdminBetaId = adminBeta.Id;
        var clienteA = await Usuario("clientea@alpha.test", AgenciaAlpha, TipoUsuario.Cliente, "Cliente");
        var clienteA2 = await Usuario("clientea2@alpha.test", AgenciaAlpha, TipoUsuario.Cliente, "Cliente");
        await Usuario("restrito@alpha.test", AgenciaAlpha, TipoUsuario.Equipe, "Equipe", "Tarefas");

        var ca = new Cliente { AgenciaId = AgenciaAlpha, NomeEmpresa = "Padaria Alpha", Responsavel = "A", Email = "a@a", UsuarioClienteId = clienteA.Id };
        var ca2 = new Cliente { AgenciaId = AgenciaAlpha, NomeEmpresa = "Oficina Segredo", Responsavel = "A2", Email = "a2@a", UsuarioClienteId = clienteA2.Id };
        var cb = new Cliente { AgenciaId = AgenciaBeta, NomeEmpresa = "Clinica Beta Confidencial", Responsavel = "B", Email = "b@b" };
        db.Clientes.AddRange(ca, ca2, cb);
        await db.SaveChangesAsync();
        ClienteA = ca.Id; ClienteA2 = ca2.Id; ClienteBeta = cb.Id;

        var ta = new Tarefa { AgenciaId = AgenciaAlpha, Titulo = "Tarefa da Padaria", ClienteId = ca.Id };
        var ta2 = new Tarefa { AgenciaId = AgenciaAlpha, Titulo = "Tarefa da Oficina", ClienteId = ca2.Id };
        var tb = new Tarefa { AgenciaId = AgenciaBeta, Titulo = "Tarefa original Beta", ClienteId = cb.Id, ResponsavelId = adminBeta.Id };
        var conteudoA2 = new Conteudo { AgenciaId = AgenciaAlpha, Titulo = "Post da Oficina", ClienteId = ca2.Id, DataPrevista = DateTime.UtcNow };
        var conteudoB = new Conteudo { AgenciaId = AgenciaBeta, Titulo = "Post Beta", ClienteId = cb.Id, DataPrevista = DateTime.UtcNow };
        db.AddRange(ta, ta2, tb, conteudoA2, conteudoB);
        await db.SaveChangesAsync();
        TarefaA = ta.Id; TarefaA2 = ta2.Id; TarefaBeta = tb.Id; ConteudoA2 = conteudoA2.Id; ConteudoBeta = conteudoB.Id;
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>Contexto sem filtro para conferir o estado real do banco.</summary>
    public ApplicationDbContext BancoSistema(out IServiceScope escopo)
    {
        escopo = Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema();
        return escopo.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    }

    public async Task<Navegador> LoginAsync(string email)
    {
        var nav = new Navegador(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));
        var r = await nav.PostFormAsync("/Account/Login", "/Account/Login", new() { ["Email"] = email, ["Senha"] = Senha });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains(".AspNetCore.Identity.Application", string.Join(";", r.Headers.GetValues("Set-Cookie")));
        return nav;
    }
}

/// <summary>Cliente HTTP que busca o token antiforgery de uma página antes de enviar o formulário.</summary>
public sealed class Navegador(HttpClient http)
{
    public HttpClient Http => http;

    public Task<HttpResponseMessage> GetAsync(string url) => http.GetAsync(url);

    public async Task<string> TokenAsync(string paginaComFormulario)
    {
        var html = await (await http.GetAsync(paginaComFormulario)).Content.ReadAsStringAsync();
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        Assert.True(m.Success, $"Token antiforgery não encontrado em {paginaComFormulario}");
        return m.Groups[1].Value;
    }

    public async Task<HttpResponseMessage> PostFormAsync(string url, string paginaDoToken, Dictionary<string, string> campos)
    {
        campos["__RequestVerificationToken"] = await TokenAsync(paginaDoToken);
        return await http.PostAsync(url, new FormUrlEncodedContent(campos));
    }
}

[CollectionDefinition("http")]
public class ColecaoHttp : ICollectionFixture<AppMultiTenant>;

[Collection("http")]
[Trait("Categoria", "Integracao")]
public class TestesHttpMultiTenant(AppMultiTenant app)
{
    private static bool Negado(HttpResponseMessage r) =>
        r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
        || (r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location?.OriginalString.Contains("AcessoNegado") == true);

    // Cenário 1: Agência Alpha tenta consultar clientes da Agência Beta
    [Fact]
    public async Task Cenario1_Alpha_nao_consulta_clientes_da_Beta()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");

        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/Clientes/Details/{app.ClienteBeta}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/Clientes/Edit/{app.ClienteBeta}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alpha.GetAsync($"/Calendario/Details/{app.ConteudoBeta}")).StatusCode);

        var lista = await (await alpha.GetAsync("/Clientes")).Content.ReadAsStringAsync();
        Assert.Contains("Padaria Alpha", lista);
        Assert.DoesNotContain("Clinica Beta Confidencial", lista);

        var hoje = DateTime.UtcNow;
        var eventos = await (await alpha.GetAsync($"/Calendario/Eventos?clienteId={app.ClienteBeta}&mes={hoje.Month}&ano={hoje.Year}")).Content.ReadAsStringAsync();
        Assert.Equal("[]", eventos);

        var tarefas = await (await alpha.GetAsync("/Tarefas")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Tarefa original Beta", tarefas);
    }

    // Cenário 2: usuário da Alpha tenta modificar uma tarefa da Beta
    [Fact]
    public async Task Cenario2_Alpha_nao_modifica_tarefa_da_Beta()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");

        var edit = await alpha.PostFormAsync("/Tarefas/Edit", "/Tarefas/Create", new()
        {
            ["Id"] = app.TarefaBeta.ToString(), ["Titulo"] = "INVADIDO", ["Status"] = "Concluido",
            ["Tipo"] = "Outro", ["Prioridade"] = "Media", ["ClienteId"] = app.ClienteA.ToString()
        });
        Assert.True(Negado(edit), $"Edit retornou {edit.StatusCode}");

        var status = await alpha.PostFormAsync($"/Tarefas/AlterarStatus/{app.TarefaBeta}", "/Tarefas/Create", new() { ["status"] = "Concluido" });
        Assert.True(Negado(status), $"AlterarStatus retornou {status.StatusCode}");

        var editCliente = await alpha.PostFormAsync("/Clientes/Edit", "/Clientes/Create", new()
        {
            ["Id"] = app.ClienteBeta.ToString(), ["NomeEmpresa"] = "INVADIDO", ["Responsavel"] = "x", ["Email"] = "x@x.com"
        });
        Assert.True(Negado(editCliente), $"Clientes/Edit retornou {editCliente.StatusCode}");

        using var db = app.BancoSistema(out var escopo);
        using (escopo)
        {
            var tarefa = await db.Tarefas.SingleAsync(t => t.Id == app.TarefaBeta);
            Assert.Equal("Tarefa original Beta", tarefa.Titulo);
            Assert.Equal(StatusTarefa.Pendente, tarefa.Status);
            Assert.Equal("Clinica Beta Confidencial", (await db.Clientes.SingleAsync(c => c.Id == app.ClienteBeta)).NomeEmpresa);
        }
    }

    // Cenário 3: Cliente A tenta visualizar informações do Cliente B na mesma agência
    [Fact]
    public async Task Cenario3_Cliente_nao_ve_outro_cliente_da_mesma_agencia()
    {
        var clienteA = await app.LoginAsync("clientea@alpha.test");

        Assert.Equal(HttpStatusCode.NotFound, (await clienteA.GetAsync($"/Clientes/Details/{app.ClienteA2}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await clienteA.GetAsync($"/Clientes/Details/{app.ClienteA}")).StatusCode);

        // A lista de clientes leva direto ao próprio cliente
        var lista = await clienteA.GetAsync("/Clientes");
        Assert.Equal(HttpStatusCode.Redirect, lista.StatusCode);
        Assert.EndsWith($"/Clientes/Details/{app.ClienteA}", lista.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.NotFound, (await clienteA.GetAsync($"/Calendario/Details/{app.ConteudoA2}")).StatusCode);
        var hoje = DateTime.UtcNow;
        Assert.Equal("[]", await (await clienteA.GetAsync($"/Calendario/Eventos?clienteId={app.ClienteA2}&mes={hoje.Month}&ano={hoje.Year}")).Content.ReadAsStringAsync());

        var tarefas = await (await clienteA.GetAsync("/Tarefas")).Content.ReadAsStringAsync();
        Assert.Contains("Tarefa da Padaria", tarefas);
        Assert.DoesNotContain("Tarefa da Oficina", tarefas);

        var calendario = await (await clienteA.GetAsync("/Calendario")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Post da Oficina", calendario);

        var comentar = await clienteA.PostFormAsync("/Calendario/Comentar", "/Arquivos", new() { ["conteudoId"] = app.ConteudoA2.ToString(), ["texto"] = "oi" });
        Assert.True(Negado(comentar), $"Comentar retornou {comentar.StatusCode}");

        var status = await clienteA.PostFormAsync($"/Tarefas/AlterarStatus/{app.TarefaA}", "/Arquivos", new() { ["status"] = "Concluido" });
        Assert.True(Negado(status), $"Cliente alterou status: {status.StatusCode}");
    }

    // Cenário 4: usuário tenta alterar o AgencyId manualmente na requisição
    [Fact]
    public async Task Cenario4_AgenciaId_forjado_na_requisicao_nao_tem_efeito()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");

        var criar = await alpha.PostFormAsync("/Clientes/Create", "/Clientes/Create", new()
        {
            ["NomeEmpresa"] = "Cliente Forjado", ["Responsavel"] = "x", ["Email"] = "f@f.com",
            ["AgenciaId"] = app.AgenciaBeta.ToString(), ["Ativo"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, criar.StatusCode);

        // Tarefa da Alpha apontando para cliente da Beta
        var tarefa = await alpha.PostFormAsync("/Tarefas/Create", "/Tarefas/Create", new()
        {
            ["Titulo"] = "Tarefa cruzada", ["Tipo"] = "Outro", ["Prioridade"] = "Media", ["Status"] = "Pendente",
            ["ClienteId"] = app.ClienteBeta.ToString(), ["AgenciaId"] = app.AgenciaBeta.ToString()
        });
        Assert.True(Negado(tarefa), $"Tarefa cruzada retornou {tarefa.StatusCode}");

        // Query string/rota com agência não muda nada: a agência vem do usuário
        var lista = await (await alpha.GetAsync($"/Clientes?agenciaId={app.AgenciaBeta}&AgenciaId={app.AgenciaBeta}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Clinica Beta Confidencial", lista);

        using var db = app.BancoSistema(out var escopo);
        using (escopo)
        {
            var forjado = await db.Clientes.SingleAsync(c => c.NomeEmpresa == "Cliente Forjado");
            Assert.Equal(app.AgenciaAlpha, forjado.AgenciaId);
            Assert.False(await db.Tarefas.AnyAsync(t => t.Titulo == "Tarefa cruzada"));
        }
    }

    // Cenário 6 (parcial, Fase 1): envio de arquivo para cliente de outra agência / outro cliente
    [Fact]
    public async Task Cenario6_Upload_para_cliente_alheio_eh_negado()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        var clienteA = await app.LoginAsync("clientea@alpha.test");

        async Task<HttpResponseMessage> Enviar(Navegador nav, int clienteId)
        {
            var token = await nav.TokenAsync("/Arquivos");
            using var form = new MultipartFormDataContent
            {
                { new StringContent(token), "__RequestVerificationToken" },
                { new StringContent(clienteId.ToString()), "clienteId" },
                { new StringContent("Bruto"), "categoria" },
                { new ByteArrayContent([1, 2, 3]), "arquivo", "teste.txt" }
            };
            return await nav.Http.PostAsync("/Arquivos/Upload", form);
        }

        Assert.True(Negado(await Enviar(alpha, app.ClienteBeta)));
        Assert.True(Negado(await Enviar(clienteA, app.ClienteA2)));

        using var db = app.BancoSistema(out var escopo);
        using (escopo)
            Assert.False(await db.Arquivos.AnyAsync(a => a.ClienteId == app.ClienteBeta || a.ClienteId == app.ClienteA2));
    }

    private static async Task<HttpResponseMessage> EnviarArquivo(Navegador nav, int clienteId, string nome, byte[] conteudo)
    {
        var token = await nav.TokenAsync("/Arquivos");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(clienteId.ToString()), "clienteId" },
            { new StringContent("Bruto"), "categoria" },
            { new ByteArrayContent(conteudo), "arquivo", nome }
        };
        return await nav.Http.PostAsync("/Arquivos/Upload", form);
    }

    // Cenário 6 (completo, Fase 2): usuário tenta baixar arquivo privado de outra agência / outro cliente
    [Fact]
    public async Task Cenario6_Download_de_arquivo_privado_so_para_quem_pode_ver_o_cliente()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        var nome = $"proposta-{Guid.NewGuid():N}.pdf";
        Assert.Equal(HttpStatusCode.Redirect, (await EnviarArquivo(alpha, app.ClienteA, nome, Amostras.Pdf)).StatusCode);

        Arquivo arq;
        using (var db = app.BancoSistema(out var escopo))
        using (escopo)
            arq = await db.Arquivos.SingleAsync(a => a.NomeOriginal == nome);
        Assert.StartsWith($"agencies/{app.AgenciaAlpha}/clients/{app.ClienteA}/", arq.ChaveArmazenamento);
        Assert.Equal("application/pdf", arq.TipoMime);
        Assert.NotNull(arq.HashSha256);
        Assert.Equal("", arq.Url); // nenhum caminho público

        // Dono e cliente vinculado baixam
        foreach (var email in new[] { "admin@alpha.test", "clientea@alpha.test" })
        {
            var nav = email == "admin@alpha.test" ? alpha : await app.LoginAsync(email);
            var r = await nav.GetAsync($"/Arquivos/Baixar/{arq.Id}");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal(Amostras.Pdf, await r.Content.ReadAsByteArrayAsync());
            Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        }

        // Outra agência, outro cliente da mesma agência e anônimo: negado
        var beta = await app.LoginAsync("admin@beta.test");
        Assert.Equal(HttpStatusCode.NotFound, (await beta.GetAsync($"/Arquivos/Baixar/{arq.Id}")).StatusCode);
        var clienteA2 = await app.LoginAsync("clientea2@alpha.test");
        Assert.Equal(HttpStatusCode.NotFound, (await clienteA2.GetAsync($"/Arquivos/Baixar/{arq.Id}")).StatusCode);
        var anonimo = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r2 = await anonimo.GetAsync($"/Arquivos/Baixar/{arq.Id}");
        Assert.Equal(HttpStatusCode.Redirect, r2.StatusCode);
        Assert.Contains("/Account/Login", r2.Headers.Location!.OriginalString);

        // Beta não exclui arquivo da Alpha
        var excluir = await beta.PostFormAsync($"/Arquivos/Excluir/{arq.Id}", "/Arquivos", new());
        Assert.True(Negado(excluir));
        Assert.Equal(HttpStatusCode.OK, (await alpha.GetAsync($"/Arquivos/Baixar/{arq.Id}")).StatusCode);

        // Pasta pública antiga não serve mais nada
        Assert.Equal(HttpStatusCode.NotFound, (await anonimo.GetAsync("/uploads/1/3e56bfd5-3536-4e2a-ba8b-0b21935e1985.pdf")).StatusCode);
    }

    [Fact]
    public async Task Upload_de_executavel_disfarcado_eh_recusado()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        var nome = $"foto-{Guid.NewGuid():N}.png";
        await EnviarArquivo(alpha, app.ClienteA, nome, Amostras.Exe);

        using var db = app.BancoSistema(out var escopo);
        using (escopo)
            Assert.False(await db.Arquivos.AnyAsync(a => a.NomeOriginal == nome));
    }

    [Fact]
    public async Task Rota_de_logos_so_serve_nomes_gerados_pelo_sistema()
    {
        var anonimo = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonimo.GetAsync($"/marca/{app.AgenciaAlpha}/..%2F..%2Fclients%2F1%2Fx.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonimo.GetAsync($"/marca/{app.AgenciaAlpha}/proposta.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonimo.GetAsync($"/marca/{app.AgenciaAlpha}/logo-{new string('a', 32)}.png")).StatusCode);
    }

    [Fact]
    public async Task Admin_nao_gerencia_usuario_de_outra_agencia()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        var html = await (await alpha.GetAsync("/Usuarios")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("admin@beta.test", html);

        var r = await alpha.PostFormAsync("/Usuarios/Ativar", "/Usuarios", new() { ["id"] = app.AdminBetaId });
        Assert.True(Negado(r));

        using var db = app.BancoSistema(out var escopo);
        using (escopo)
            Assert.True((await db.Users.SingleAsync(u => u.Id == app.AdminBetaId)).Ativo);
    }

    [Fact]
    public async Task Abas_permitidas_sao_aplicadas_no_servidor()
    {
        var restrito = await app.LoginAsync("restrito@alpha.test");
        Assert.Equal(HttpStatusCode.OK, (await restrito.GetAsync("/Tarefas")).StatusCode);
        Assert.True(Negado(await restrito.GetAsync("/Clientes")));
        Assert.True(Negado(await restrito.GetAsync("/Calendario")));
        Assert.True(Negado(await restrito.GetAsync("/Financeiro")));
    }

    [Fact]
    public async Task Superadmin_fica_no_painel_da_plataforma_e_agencia_nao_acessa_o_painel()
    {
        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        var dash = await super.GetAsync("/Dashboard");
        Assert.Equal(HttpStatusCode.Redirect, dash.StatusCode);
        Assert.Contains("/Plataforma", dash.Headers.Location!.OriginalString);
        var painel = await (await super.GetAsync("/Plataforma")).Content.ReadAsStringAsync();
        Assert.Contains("<code>beta</code>", painel); // nomes com acento saem codificados no HTML

        var alpha = await app.LoginAsync("admin@alpha.test");
        Assert.True(Negado(await alpha.GetAsync("/Plataforma")));
    }

    [Fact]
    public async Task Superadmin_cria_agencia_que_nasce_isolada()
    {
        var super = await app.LoginAsync(AppMultiTenant.SuperEmail);
        var r = await super.PostFormAsync("/Plataforma/NovaAgencia", "/Plataforma/NovaAgencia", new()
        {
            ["Nome"] = "Agência Gamma", ["AdminNome"] = "Gamma", ["AdminEmail"] = "admin@gamma.test", ["AdminSenha"] = AppMultiTenant.Senha
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var gamma = await app.LoginAsync("admin@gamma.test");
        var clientes = await (await gamma.GetAsync("/Clientes")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Padaria Alpha", clientes);
        Assert.DoesNotContain("Clinica Beta Confidencial", clientes);
        Assert.Equal(HttpStatusCode.NotFound, (await gamma.GetAsync($"/Clientes/Details/{app.ClienteA}")).StatusCode);
    }

    [Fact]
    public async Task Regressao_telas_principais_da_agencia_abrem()
    {
        var alpha = await app.LoginAsync("admin@alpha.test");
        foreach (var url in new[] { "/Dashboard", "/Clientes", $"/Clientes/Details/{app.ClienteA}", "/Clientes/Create", $"/Clientes/Edit/{app.ClienteA}",
                     "/Tarefas", "/Tarefas/Create", $"/Tarefas/Edit/{app.TarefaA}", "/Calendario", "/Calendario/Create", "/Arquivos",
                     "/Financeiro", "/Financeiro/ContasReceber", "/Financeiro/ContasPagar", "/Financeiro/Relatorio", "/Contratos",
                     "/DatasComemorativas", "/Usuarios", "/Configuracao", "/Notificacoes", "/Marketing", "/Integracoes" })
        {
            var r = await alpha.GetAsync(url);
            Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} retornou {(int)r.StatusCode}");
        }

        var cliente = await app.LoginAsync("clientea@alpha.test");
        Assert.Equal(HttpStatusCode.OK, (await cliente.GetAsync("/Dashboard")).StatusCode);
        var login = await app.CreateClient().GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
