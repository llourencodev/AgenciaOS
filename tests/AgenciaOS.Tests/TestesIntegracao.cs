using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing;
using AgenciaOS.Services.Marketing.Provedores;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>
/// Banco SQLite em memória com o modelo real do AgenciaOS. Cria as agências 1 (Alpha) e 2 (Beta);
/// os contextos operam como usuário da agência indicada (padrão: 1), igual a uma requisição web.
/// </summary>
public sealed class BancoTeste : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _opcoes;
    public ApplicationDbContext Db { get; }

    public BancoTeste()
    {
        _conn.Open();
        _opcoes = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_conn).Options;
        using (var sistema = new ApplicationDbContext(_opcoes))
        {
            sistema.Database.EnsureCreated();
            sistema.Agencias.AddRange(
                new Agencia { Id = 1, Nome = "Agência Alpha", Slug = "alpha" },
                new Agencia { Id = 2, Nome = "Agência Beta", Slug = "beta" });
            sistema.SaveChanges();
        }
        Db = NovoContexto();
    }

    public ApplicationDbContext NovoContexto(int agenciaId = 1) => new(_opcoes, TenantContext.ParaAgencia(agenciaId));

    /// <summary>Contexto sem filtro (worker, seed, superadmin).</summary>
    public ApplicationDbContext ContextoSistema()
    {
        var t = new TenantContext();
        t.UsarSistema();
        return new(_opcoes, t);
    }

    /// <summary>Contexto que usa um TenantContext compartilhado com o serviço testado.</summary>
    public ApplicationDbContext Contexto(TenantContext tenant) => new(_opcoes, tenant);

    /// <summary>Contexto sem agência (anônimo / superadmin fora do painel).</summary>
    public ApplicationDbContext ContextoSemAgencia() => new(_opcoes, new TenantContext());

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}

public class IsolamentoTests
{
    private static (Usuario Admin, Usuario ClienteA, Usuario ClienteB, Usuario Colab, Usuario EquipeSemAba, Cliente A, Cliente B) Popular(ApplicationDbContext db)
    {
        Usuario U(string id, TipoUsuario t, string? abas = null) => new() { Id = id, UserName = id, Email = id + "@t.com", Nome = id, Tipo = t, AbasPermitidas = abas };
        var admin = U("admin", TipoUsuario.Admin);
        var ua = U("ua", TipoUsuario.Cliente);
        var ub = U("ub", TipoUsuario.Cliente);
        var colab = U("colab", TipoUsuario.ColaboradorExterno);
        var equipe = U("equipe", TipoUsuario.Equipe, "Tarefas,Calendario");
        db.Users.AddRange(admin, ua, ub, colab, equipe);
        var a = new Cliente { NomeEmpresa = "Empresa A", Responsavel = "A", Email = "a@a.com", UsuarioClienteId = "ua" };
        var b = new Cliente { NomeEmpresa = "Empresa B", Responsavel = "B", Email = "b@b.com", UsuarioClienteId = "ub" };
        db.Clientes.AddRange(a, b);
        db.SaveChanges();

        foreach (var c in new[] { a, b })
        {
            var conexao = new ConexaoMarketing { ClienteId = c.Id, Plataforma = PlataformaMarketing.Instagram, TokenAcessoProtegido = "x", Status = StatusIntegracao.Conectado };
            db.ConexoesMarketing.Add(conexao);
            db.SaveChanges();
            db.ContasMarketing.Add(new ContaMarketing { ClienteId = c.Id, ConexaoMarketingId = conexao.Id, Plataforma = PlataformaMarketing.Instagram, IdExterno = "ig-" + c.Id, Nome = c.NomeEmpresa });
        }
        db.SaveChanges();
        return (admin, ua, ub, colab, equipe, a, b);
    }

    [Fact]
    public async Task Cliente_ve_somente_os_proprios_dados()
    {
        using var banco = new BancoTeste();
        var (_, ua, _, _, _, a, b) = Popular(banco.Db);
        var acesso = new AcessoMarketingService(banco.Db);

        var ctx = await acesso.ObterAsync(ua, ehAdmin: false, ehEquipe: false);
        Assert.True(ctx.PodeAcessarModulo);
        Assert.False(ctx.EhAgencia);
        Assert.True(ctx.PodeAcessarCliente(a.Id));
        Assert.False(ctx.PodeAcessarCliente(b.Id)); // trocar o clienteId na requisição não dá acesso

        var clientes = await acesso.Clientes(ctx).Select(c => c.Id).ToListAsync();
        Assert.Equal([a.Id], clientes);
        var contas = await acesso.Contas(ctx).Select(c => c.ClienteId).Distinct().ToListAsync();
        Assert.Equal([a.Id], contas);
        // Mesmo pedindo explicitamente a conta do outro cliente, a consulta filtrada não retorna nada
        var contaB = await banco.Db.ContasMarketing.Where(c => c.ClienteId == b.Id).Select(c => c.Id).FirstAsync();
        Assert.False(await acesso.Contas(ctx).AnyAsync(c => c.Id == contaB));
    }

    [Fact]
    public async Task Agencia_ve_todos_e_colaborador_ou_equipe_sem_aba_nao_acessam()
    {
        using var banco = new BancoTeste();
        var (admin, _, _, colab, equipe, a, b) = Popular(banco.Db);
        var acesso = new AcessoMarketingService(banco.Db);

        var ctxAdmin = await acesso.ObterAsync(admin, ehAdmin: true, ehEquipe: false);
        Assert.True(ctxAdmin.EhAgencia);
        Assert.Equal(2, await acesso.Contas(ctxAdmin).CountAsync());
        Assert.True(ctxAdmin.PodeAcessarCliente(a.Id) && ctxAdmin.PodeAcessarCliente(b.Id));

        var ctxColab = await acesso.ObterAsync(colab, false, false);
        Assert.False(ctxColab.PodeAcessarModulo);
        Assert.Equal(0, await acesso.Contas(ctxColab).CountAsync());

        var ctxEquipe = await acesso.ObterAsync(equipe, false, ehEquipe: true);
        Assert.False(ctxEquipe.PodeAcessarModulo); // sem a aba "Marketing" nas permissões
    }

    [Fact]
    public async Task Cliente_inativo_ou_sem_vinculo_nao_acessa()
    {
        using var banco = new BancoTeste();
        var (_, ua, _, _, _, a, _) = Popular(banco.Db);
        a.Ativo = false;
        banco.Db.SaveChanges();
        var ctx = await new AcessoMarketingService(banco.Db).ObterAsync(ua, false, false);
        Assert.False(ctx.PodeAcessarModulo);
    }
}

public class SincronizacaoTests
{
    private sealed class ProvedorFalso(PlataformaMarketing plataforma) : IProvedorMarketing
    {
        public Func<DadosSincronizados>? Resposta { get; set; }
        public ErroApiExterna? Erro { get; set; }
        public int Renovacoes { get; private set; }
        public bool ExigirRenovacao { get; set; }
        public PlataformaMarketing Plataforma => plataforma;
        public string Fonte => "Teste";
        public string MontarUrlAutorizacao(string s, string r, string c) => "";
        public Task<ResultadoToken> TrocarCodigoAsync(string code, string r, string v, CancellationToken ct) => throw new NotImplementedException();
        public Task<ResultadoToken?> RenovarAsync(ConexaoMarketing c, string a, string? r, CancellationToken ct)
        {
            if (!ExigirRenovacao) return Task.FromResult<ResultadoToken?>(null);
            Renovacoes++;
            return Task.FromResult<ResultadoToken?>(new ResultadoToken("novo-token", "novo-refresh", DateTime.UtcNow.AddHours(1), null, "s", null, null));
        }
        public Task<List<ContaDisponivel>> ListarContasAsync(string a, CancellationToken ct) => Task.FromResult(new List<ContaDisponivel>());
        public Task<DadosSincronizados> SincronizarAsync(ContaMarketing conta, string token, DateOnly d, DateOnly a, bool i, CancellationToken ct)
        {
            if (Erro != null) throw Erro;
            return Task.FromResult(Resposta!());
        }
        public Task<bool> RevogarAsync(string a, CancellationToken ct) => Task.FromResult(true);
    }

    private static (SincronizacaoMarketingService Svc, ProvedorFalso Prov, ProtetorTokens Prot) Montar(ApplicationDbContext db)
    {
        var prov = new ProvedorFalso(PlataformaMarketing.Instagram);
        var prot = new ProtetorTokens(new EphemeralDataProtectionProvider());
        var svc = new SincronizacaoMarketingService(db, new RegistroProvedores([prov]), prot, NullLogger<SincronizacaoMarketingService>.Instance);
        return (svc, prov, prot);
    }

    private static int CriarConta(ApplicationDbContext db, ProtetorTokens prot)
    {
        var cliente = new Cliente { NomeEmpresa = "C", Responsavel = "R", Email = "c@c.com" };
        db.Clientes.Add(cliente);
        db.SaveChanges();
        var conexao = new ConexaoMarketing
        {
            ClienteId = cliente.Id, Plataforma = PlataformaMarketing.Instagram, Status = StatusIntegracao.Conectado,
            TokenAcessoProtegido = prot.Proteger("token-original"), ExpiraEm = DateTime.UtcNow.AddDays(30)
        };
        db.ConexoesMarketing.Add(conexao);
        db.SaveChanges();
        var conta = new ContaMarketing { ClienteId = cliente.Id, ConexaoMarketingId = conexao.Id, Plataforma = PlataformaMarketing.Instagram, IdExterno = "1", Nome = "perfil" };
        db.ContasMarketing.Add(conta);
        db.SaveChanges();
        return conta.Id;
    }

    private static DadosSincronizados Dados(decimal seguidores)
    {
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var d = new DadosSincronizados { Nome = "Perfil Atualizado", NomeUsuario = "perfil" };
        d.Adicionar(hoje, Metricas.Seguidores, seguidores);
        d.Adicionar(hoje.AddDays(-1), Metricas.Alcance, 300);
        d.Adicionar(hoje, Metricas.Alcance, 500);
        d.Dimensoes.Add(new DimensaoColetada(hoje, Dimensoes.Pais, "Brazil", Metricas.Sessoes, 10));
        d.Publicacoes.Add(new PublicacaoColetada { IdExterno = "post-1", PublicadoEm = DateTime.UtcNow, Curtidas = 10, Interacoes = 12 });
        d.Indisponiveis.Add(Metricas.VisitasPerfil);
        return d;
    }

    [Fact]
    public async Task Grava_historico_sem_duplicar_e_atualiza_status()
    {
        using var banco = new BancoTeste();
        var (svc, prov, prot) = Montar(banco.Db);
        var contaId = CriarConta(banco.Db, prot);

        prov.Resposta = () => Dados(1000);
        var h1 = await svc.SincronizarAsync(contaId, TipoSincronizacao.Manual, default);
        Assert.True(h1!.Sucesso);
        Assert.Equal(TipoSincronizacao.Inicial, h1.Tipo);

        prov.Resposta = () => Dados(1010);
        var h2 = await svc.SincronizarAsync(contaId, TipoSincronizacao.Agendada, default);
        Assert.True(h2!.Sucesso);

        using var db = banco.NovoContexto();
        Assert.Equal(3, await db.MetricasDiarias.CountAsync(m => m.ContaMarketingId == contaId)); // 1 seguidores + 2 alcance (sem duplicar)
        Assert.Equal(1010m, (await db.MetricasDiarias.SingleAsync(m => m.Metrica == Metricas.Seguidores)).Valor);
        Assert.Equal(1, await db.MetricasDimensao.CountAsync());
        Assert.Equal(1, await db.PublicacoesMarketing.CountAsync());
        Assert.Equal(2, await db.HistoricosSincronizacao.CountAsync());

        var conta = await db.ContasMarketing.SingleAsync(c => c.Id == contaId);
        Assert.Equal(StatusIntegracao.Conectado, conta.Status);
        Assert.Equal("Perfil Atualizado", conta.Nome);
        Assert.Equal(Metricas.VisitasPerfil, conta.MetricasIndisponiveis);
        Assert.NotNull(conta.ProximaSincronizacao);
    }

    [Fact]
    public async Task Token_invalido_marca_expirado_e_para_de_agendar()
    {
        using var banco = new BancoTeste();
        var (svc, prov, prot) = Montar(banco.Db);
        var contaId = CriarConta(banco.Db, prot);
        prov.Erro = new ErroApiExterna(TipoErroApi.TokenInvalido, "Error validating access token: access_token=EAAsecretsecretsecret123");

        var h = await svc.SincronizarAsync(contaId, TipoSincronizacao.Manual, default);
        Assert.False(h!.Sucesso);
        Assert.DoesNotContain("EAAsecret", h.Mensagem);

        using var db = banco.NovoContexto();
        var conta = await db.ContasMarketing.Include(c => c.Conexao).SingleAsync(c => c.Id == contaId);
        Assert.Equal(StatusIntegracao.TokenExpirado, conta.Status);
        Assert.Equal(StatusIntegracao.TokenExpirado, conta.Conexao!.Status);
        Assert.Null(conta.ProximaSincronizacao);

        // Com a autorização expirada, novas tentativas não chamam a API
        Assert.Null(await svc.SincronizarAsync(contaId, TipoSincronizacao.Agendada, default));
    }

    [Fact]
    public async Task Falha_temporaria_agenda_retentativa_com_backoff_e_nao_grava_parcial()
    {
        using var banco = new BancoTeste();
        var (svc, prov, prot) = Montar(banco.Db);
        var contaId = CriarConta(banco.Db, prot);

        prov.Erro = new ErroApiExterna(TipoErroApi.Temporario, "Serviço indisponível");
        await svc.SincronizarAsync(contaId, TipoSincronizacao.Agendada, default);
        var primeira = (await banco.NovoContexto().ContasMarketing.SingleAsync(c => c.Id == contaId)).ProximaSincronizacao!.Value;

        await svc.SincronizarAsync(contaId, TipoSincronizacao.Agendada, default);
        using var db = banco.NovoContexto();
        var conta = await db.ContasMarketing.SingleAsync(c => c.Id == contaId);
        Assert.Equal(StatusIntegracao.ErroSincronizacao, conta.Status);
        Assert.Equal(2, conta.FalhasConsecutivas);
        Assert.True(conta.ProximaSincronizacao > primeira); // espera cresce (15 min → 30 min)
        Assert.Equal(0, await db.MetricasDiarias.CountAsync());
    }

    [Fact]
    public async Task Limite_de_taxa_respeita_retry_after()
    {
        using var banco = new BancoTeste();
        var (svc, prov, prot) = Montar(banco.Db);
        var contaId = CriarConta(banco.Db, prot);
        prov.Erro = new ErroApiExterna(TipoErroApi.LimiteTaxa, "rate limit", TimeSpan.FromHours(2));

        await svc.SincronizarAsync(contaId, TipoSincronizacao.Agendada, default);
        var conta = await banco.NovoContexto().ContasMarketing.SingleAsync(c => c.Id == contaId);
        Assert.InRange(conta.ProximaSincronizacao!.Value, DateTime.UtcNow.AddMinutes(115), DateTime.UtcNow.AddMinutes(125));
    }

    [Fact]
    public async Task Renova_token_e_grava_criptografado()
    {
        using var banco = new BancoTeste();
        var (svc, prov, prot) = Montar(banco.Db);
        var contaId = CriarConta(banco.Db, prot);
        prov.ExigirRenovacao = true;
        prov.Resposta = () => Dados(1);

        await svc.SincronizarAsync(contaId, TipoSincronizacao.Manual, default);
        Assert.Equal(1, prov.Renovacoes);
        var conexao = await banco.NovoContexto().ConexoesMarketing.SingleAsync();
        Assert.DoesNotContain("novo-token", conexao.TokenAcessoProtegido);
        Assert.Equal("novo-token", prot.Revelar(conexao.TokenAcessoProtegido));
        Assert.Equal("novo-refresh", prot.Revelar(conexao.TokenRefreshProtegido!));
    }
}
