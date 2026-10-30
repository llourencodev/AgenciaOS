using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenciaOS.Tests;

/// <summary>
/// Isolamento entre agências no nível do DbContext (filtros globais + guarda de escrita).
/// Agência 1 = Alpha, 2 = Beta (ver <see cref="BancoTeste"/>).
/// </summary>
public class TestesMultiTenant
{
    private record Dados(int ClienteAlpha, int ClienteBeta, int TarefaAlpha, int TarefaBeta, int ConteudoBeta, int ContaBeta);

    private static Dados Popular(BancoTeste banco)
    {
        using var db = banco.ContextoSistema();
        db.Users.AddRange(
            new Usuario { Id = "u-alpha", UserName = "a@a", Email = "a@a", Nome = "Alpha", AgenciaId = 1 },
            new Usuario { Id = "u-beta", UserName = "b@b", Email = "b@b", Nome = "Beta", AgenciaId = 2 });
        var ca = new Cliente { AgenciaId = 1, NomeEmpresa = "Cliente Alpha", Responsavel = "x", Email = "x@a" };
        var cb = new Cliente { AgenciaId = 2, NomeEmpresa = "Cliente Beta", Responsavel = "y", Email = "y@b" };
        db.Clientes.AddRange(ca, cb);
        db.SaveChanges();

        var ta = new Tarefa { AgenciaId = 1, Titulo = "Tarefa Alpha", ClienteId = ca.Id, ResponsavelId = "u-alpha" };
        var tb = new Tarefa { AgenciaId = 2, Titulo = "Tarefa Beta", ClienteId = cb.Id, ResponsavelId = "u-beta" };
        var conteudoB = new Conteudo { AgenciaId = 2, Titulo = "Post Beta", ClienteId = cb.Id, DataPrevista = DateTime.UtcNow };
        db.AddRange(ta, tb, conteudoB);
        db.SaveChanges();
        db.ComentariosConteudo.Add(new ComentarioConteudo { ConteudoId = conteudoB.Id, UsuarioId = "u-beta", Texto = "segredo da Beta" });

        var conexao = new ConexaoMarketing { AgenciaId = 2, ClienteId = cb.Id, Plataforma = PlataformaMarketing.Instagram, TokenAcessoProtegido = "x" };
        db.ConexoesMarketing.Add(conexao);
        db.SaveChanges();
        var conta = new ContaMarketing { AgenciaId = 2, ClienteId = cb.Id, ConexaoMarketingId = conexao.Id, Plataforma = PlataformaMarketing.Instagram, IdExterno = "ig-b", Nome = "B" };
        db.ContasMarketing.Add(conta);
        db.SaveChanges();
        db.MetricasDiarias.Add(new MetricaDiaria { ContaMarketingId = conta.Id, ClienteId = cb.Id, Plataforma = PlataformaMarketing.Instagram, Data = DateOnly.FromDateTime(DateTime.UtcNow), Metrica = "alcance", Valor = 10 });
        db.SaveChanges();

        return new Dados(ca.Id, cb.Id, ta.Id, tb.Id, conteudoB.Id, conta.Id);
    }

    // Cenário 1: Agência Alpha tenta consultar clientes da Agência Beta
    [Fact]
    public async Task Cenario1_Alpha_nao_ve_clientes_nem_dados_da_Beta()
    {
        using var banco = new BancoTeste();
        var d = Popular(banco);
        using var alpha = banco.NovoContexto(1);

        Assert.Equal(["Cliente Alpha"], await alpha.Clientes.Select(c => c.NomeEmpresa).ToListAsync());
        Assert.Null(await alpha.Clientes.FindAsync(d.ClienteBeta));
        Assert.Null(await alpha.Clientes.FirstOrDefaultAsync(c => c.Id == d.ClienteBeta));
        Assert.Equal(0, await alpha.Tarefas.CountAsync(t => t.ClienteId == d.ClienteBeta));
        Assert.Empty(await alpha.Conteudos.Where(c => c.Id == d.ConteudoBeta).ToListAsync());
        // Filhos sem coluna própria também ficam isolados
        Assert.Empty(await alpha.ComentariosConteudo.ToListAsync());
        Assert.Empty(await alpha.MetricasDiarias.ToListAsync());
        Assert.Empty(await alpha.ContasMarketing.ToListAsync());
        // Include a partir de um registro próprio não traz dados de outra agência
        var comInclude = await alpha.Clientes.Include(c => c.Tarefas).ToListAsync();
        Assert.All(comInclude.SelectMany(c => c.Tarefas), t => Assert.Equal(1, t.AgenciaId));
    }

    // Cenário 2: usuário da Alpha tenta modificar uma tarefa da Beta
    [Fact]
    public async Task Cenario2_Alpha_nao_altera_nem_exclui_tarefa_da_Beta()
    {
        using var banco = new BancoTeste();
        var d = Popular(banco);

        // a) entidade desconectada com Id forjado (o antigo db.Tarefas.Update(model) do controller)
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.Tarefas.Update(new Tarefa { Id = d.TarefaBeta, AgenciaId = 1, Titulo = "invadido" });
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // b) exclusão por Id forjado
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.Tarefas.Remove(new Tarefa { Id = d.TarefaBeta, AgenciaId = 1 });
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // c) atualização em massa respeita o filtro
        using (var alpha = banco.NovoContexto(1))
        {
            var afetadas = await alpha.Tarefas.Where(t => t.Id == d.TarefaBeta)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Titulo, "invadido"));
            Assert.Equal(0, afetadas);
        }

        using var sistema = banco.ContextoSistema();
        var tarefa = await sistema.Tarefas.SingleAsync(t => t.Id == d.TarefaBeta);
        Assert.Equal("Tarefa Beta", tarefa.Titulo);
        Assert.Equal(2, tarefa.AgenciaId);
    }

    // Cenário 4: usuário tenta alterar o AgencyId manualmente
    [Fact]
    public async Task Cenario4_AgenciaId_forjado_eh_recusado()
    {
        using var banco = new BancoTeste();
        var d = Popular(banco);

        // Criar registro já apontando para outra agência
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.Clientes.Add(new Cliente { AgenciaId = 2, NomeEmpresa = "Infiltrado", Responsavel = "z", Email = "z@z" });
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Mover um registro próprio para outra agência
        using (var alpha = banco.NovoContexto(1))
        {
            var tarefa = await alpha.Tarefas.SingleAsync(t => t.Id == d.TarefaAlpha);
            tarefa.AgenciaId = 2;
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Sem AgenciaId: o servidor carimba a agência do usuário
        using (var alpha = banco.NovoContexto(1))
        {
            var novo = new Cliente { NomeEmpresa = "Novo", Responsavel = "n", Email = "n@n" };
            alpha.Clientes.Add(novo);
            await alpha.SaveChangesAsync();
            Assert.Equal(1, novo.AgenciaId);
        }
    }

    [Fact]
    public async Task Referencias_para_registros_de_outra_agencia_sao_recusadas()
    {
        using var banco = new BancoTeste();
        var d = Popular(banco);

        // Tarefa da Alpha apontando para cliente da Beta
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.Tarefas.Add(new Tarefa { Titulo = "x", ClienteId = d.ClienteBeta });
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Responsável de outra agência
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.Tarefas.Add(new Tarefa { Titulo = "x", ClienteId = d.ClienteAlpha, ResponsavelId = "u-beta" });
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Trocar o cliente de uma tarefa existente para um cliente da Beta
        using (var alpha = banco.NovoContexto(1))
        {
            var t = await alpha.Tarefas.SingleAsync(x => x.Id == d.TarefaAlpha);
            t.ClienteId = d.ClienteBeta;
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Comentar conteúdo da Beta (entidade filha sem AgenciaId)
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.ComentariosConteudo.Add(new ComentarioConteudo { ConteudoId = d.ConteudoBeta, UsuarioId = "u-alpha", Texto = "oi" });
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Vincular usuário da Beta como login de um cliente da Alpha
        using (var alpha = banco.NovoContexto(1))
        {
            var c = await alpha.Clientes.SingleAsync(x => x.Id == d.ClienteAlpha);
            c.UsuarioClienteId = "u-beta";
            await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());
        }
        // Referências válidas continuam funcionando
        using (var alpha = banco.NovoContexto(1))
        {
            alpha.Tarefas.Add(new Tarefa { Titulo = "ok", ClienteId = d.ClienteAlpha, ResponsavelId = "u-alpha" });
            await alpha.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Usuarios_de_outra_agencia_nao_podem_ser_alterados()
    {
        using var banco = new BancoTeste();
        Popular(banco);
        using var alpha = banco.NovoContexto(1);

        var beta = await alpha.Users.SingleAsync(u => u.Id == "u-beta"); // Usuario não tem filtro global (Identity)
        beta.Ativo = false;
        await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => alpha.SaveChangesAsync());

        var listados = await alpha.Users.DaAgencia(TenantContext.ParaAgencia(1)).Select(u => u.Id).ToListAsync();
        Assert.Equal(["u-alpha"], listados);
    }

    [Fact]
    public async Task Sem_agencia_nada_eh_visivel_nem_gravavel()
    {
        using var banco = new BancoTeste();
        Popular(banco);
        using var anonimo = banco.ContextoSemAgencia();

        Assert.Empty(await anonimo.Clientes.ToListAsync());
        Assert.Empty(await anonimo.Configuracoes.ToListAsync());
        anonimo.Clientes.Add(new Cliente { NomeEmpresa = "x", Responsavel = "x", Email = "x" });
        await Assert.ThrowsAsync<AcessoEntreAgenciasException>(() => anonimo.SaveChangesAsync());
    }

    [Fact]
    public async Task Modo_sistema_enxerga_todas_as_agencias()
    {
        using var banco = new BancoTeste();
        Popular(banco);
        using var sistema = banco.ContextoSistema();
        Assert.Equal(2, await sistema.Clientes.CountAsync());
        Assert.Equal(1, await sistema.MetricasDiarias.CountAsync());
    }

    [Fact]
    public async Task Beta_continua_vendo_os_proprios_dados()
    {
        using var banco = new BancoTeste();
        var d = Popular(banco);
        using var beta = banco.NovoContexto(2);
        Assert.Equal(["Cliente Beta"], await beta.Clientes.Select(c => c.NomeEmpresa).ToListAsync());
        Assert.Single(await beta.ComentariosConteudo.ToListAsync());
        Assert.Single(await beta.MetricasDiarias.Where(m => m.ContaMarketingId == d.ContaBeta).ToListAsync());
    }
}
