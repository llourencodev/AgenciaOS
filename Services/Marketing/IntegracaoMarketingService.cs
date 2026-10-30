using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing.Provedores;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Marketing;

/// <summary>Fluxo de conexão: callback OAuth, vínculo de contas, atualização manual e desconexão.</summary>
public class IntegracaoMarketingService(
    ApplicationDbContext db,
    RegistroProvedores provedores,
    ProtetorTokens protetor,
    FilaSincronizacao fila,
    SincronizacaoMarketingService sincronizacao,
    ILogger<IntegracaoMarketingService> logger)
{
    public static readonly TimeSpan IntervaloMinimoManual = TimeSpan.FromMinutes(5);

    /// <summary>Grava (ou atualiza) a conexão com os tokens criptografados após o OAuth.</summary>
    public async Task<ConexaoMarketing> SalvarConexaoAsync(int clienteId, PlataformaMarketing plataforma, ResultadoToken token, string usuarioId, CancellationToken ct)
    {
        var conexao = await db.ConexoesMarketing.FirstOrDefaultAsync(c => c.ClienteId == clienteId && c.Plataforma == plataforma, ct);
        if (conexao == null)
        {
            conexao = new ConexaoMarketing { ClienteId = clienteId, Plataforma = plataforma };
            db.ConexoesMarketing.Add(conexao);
        }
        conexao.TokenAcessoProtegido = protetor.Proteger(token.AccessToken);
        conexao.TokenRefreshProtegido = protetor.ProtegerOpcional(token.RefreshToken) ?? conexao.TokenRefreshProtegido;
        conexao.ExpiraEm = token.ExpiraEm;
        conexao.RefreshExpiraEm = token.RefreshExpiraEm;
        conexao.Escopos = token.Escopos;
        conexao.UsuarioExternoId = token.UsuarioExternoId;
        conexao.UsuarioExternoNome = token.UsuarioExternoNome;
        conexao.Status = StatusIntegracao.Conectando;
        conexao.UltimoErro = null;
        conexao.ConectadoPorId = usuarioId;
        conexao.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return conexao;
    }

    public async Task<List<ContaDisponivel>> ListarDisponiveisAsync(ConexaoMarketing conexao, CancellationToken ct)
    {
        var provedor = provedores.Obter(conexao.Plataforma);
        var token = await sincronizacao.ObterTokenValidoAsync(conexao, provedor, ct);
        return await provedor.ListarContasAsync(token, ct);
    }

    /// <summary>
    /// Vincula as contas escolhidas. Os IDs vêm do formulário, mas são validados contra a lista
    /// devolvida pelo provedor (o usuário não consegue vincular recurso que não autorizou).
    /// </summary>
    public async Task<int> VincularAsync(ConexaoMarketing conexao, IReadOnlyCollection<string> idsEscolhidos, CancellationToken ct)
    {
        var disponiveis = await ListarDisponiveisAsync(conexao, ct);
        var escolhidas = disponiveis.Where(d => idsEscolhidos.Contains(d.IdExterno)).ToList();
        if (!PlataformaInfo.PermiteMultiplas(conexao.Plataforma)) escolhidas = escolhidas.Take(1).ToList();
        if (escolhidas.Count == 0) return 0;

        var existentes = await db.ContasMarketing
            .Where(c => c.ClienteId == conexao.ClienteId && c.Plataforma == conexao.Plataforma)
            .ToListAsync(ct);

        // Contas não escolhidas desta plataforma deixam de sincronizar (histórico é mantido)
        foreach (var e in existentes.Where(e => escolhidas.All(x => x.IdExterno != e.IdExterno)))
        {
            e.Ativa = false;
            e.ProximaSincronizacao = null;
        }

        var paraSincronizar = new List<ContaMarketing>();
        foreach (var d in escolhidas)
        {
            var conta = existentes.FirstOrDefault(e => e.IdExterno == d.IdExterno);
            if (conta == null)
            {
                conta = new ContaMarketing { ClienteId = conexao.ClienteId, Plataforma = conexao.Plataforma, IdExterno = d.IdExterno };
                db.ContasMarketing.Add(conta);
            }
            conta.ConexaoMarketingId = conexao.Id;
            conta.Nome = d.Nome;
            conta.NomeUsuario = d.NomeUsuario;
            conta.FotoUrl = d.FotoUrl;
            conta.TipoConta = d.TipoConta;
            conta.Limitacao = d.Aviso;
            conta.TokenRecursoProtegido = protetor.ProtegerOpcional(d.TokenRecurso);
            conta.Ativa = true;
            conta.Status = StatusIntegracao.Conectado;
            conta.UltimoErro = null;
            conta.FalhasConsecutivas = 0;
            conta.ProximaSincronizacao = DateTime.UtcNow;
            paraSincronizar.Add(conta);
        }

        conexao.Status = StatusIntegracao.Conectado;
        conexao.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        foreach (var c in paraSincronizar) fila.Enfileirar(c.Id, TipoSincronizacao.Inicial);
        return paraSincronizar.Count;
    }

    /// <summary>Atualização manual (botão Atualizar). Limitada a uma a cada 5 minutos por conta.</summary>
    public async Task<string?> SolicitarAtualizacaoAsync(int contaId, CancellationToken ct)
    {
        var conta = await db.ContasMarketing.Include(c => c.Conexao).FirstOrDefaultAsync(c => c.Id == contaId, ct);
        if (conta == null || !conta.Ativa) return "Conta não encontrada.";
        if (conta.Conexao!.Status is StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada or StatusIntegracao.NaoConectado)
            return "A autorização desta conta não é mais válida. Clique em Conectar para autorizar novamente.";
        if (conta.UltimaSolicitacaoManual is DateTime u && DateTime.UtcNow - u < IntervaloMinimoManual)
            return $"Aguarde alguns minutos: a atualização manual é permitida a cada {IntervaloMinimoManual.TotalMinutes:0} minutos.";

        conta.UltimaSolicitacaoManual = DateTime.UtcNow;
        conta.Status = StatusIntegracao.Sincronizando;
        await db.SaveChangesAsync(ct);
        fila.Enfileirar(conta.Id, TipoSincronizacao.Manual);
        return null;
    }

    /// <summary>
    /// Desconecta a plataforma: revoga no provedor quando há API para isso, apaga os tokens e
    /// para as sincronizações. O histórico de métricas já coletado é mantido.
    /// </summary>
    public async Task<string> DesconectarAsync(ConexaoMarketing conexao, CancellationToken ct)
    {
        var provedor = provedores.Obter(conexao.Plataforma);
        var contas = await db.ContasMarketing.Where(c => c.ConexaoMarketingId == conexao.Id).ToListAsync(ct);
        var revogado = false;
        try
        {
            if (!string.IsNullOrEmpty(conexao.TokenAcessoProtegido))
                revogado = await provedor.RevogarAsync(protetor.Revelar(conexao.TokenAcessoProtegido), ct);
        }
        catch (Exception e)
        {
            logger.LogWarning("Revogação no provedor {Plataforma} falhou: {Erro}", conexao.Plataforma, Sanitizador.Limpar(e.Message));
        }

        conexao.TokenAcessoProtegido = "";
        conexao.TokenRefreshProtegido = null;
        conexao.ExpiraEm = null;
        conexao.RefreshExpiraEm = null;
        conexao.Status = StatusIntegracao.NaoConectado;
        conexao.UltimoErro = null;
        conexao.AtualizadoEm = DateTime.UtcNow;
        foreach (var c in contas)
        {
            c.TokenRecursoProtegido = null;
            c.Ativa = false;
            c.ProximaSincronizacao = null;
            c.Status = StatusIntegracao.NaoConectado;
        }
        await db.SaveChangesAsync(ct);

        return revogado
            ? "Conta desconectada e acesso revogado na plataforma. O histórico já coletado foi mantido."
            : "Conta desconectada e tokens apagados. Para remover o app também na plataforma, acesse as configurações de apps conectados da sua conta. O histórico já coletado foi mantido.";
    }
}
