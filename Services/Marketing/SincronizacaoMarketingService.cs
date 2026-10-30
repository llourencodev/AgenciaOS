using System.Threading.Channels;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing.Provedores;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Marketing;

public class RegistroProvedores(IEnumerable<IProvedorMarketing> provedores)
{
    private readonly Dictionary<PlataformaMarketing, IProvedorMarketing> _mapa = provedores.ToDictionary(p => p.Plataforma);
    public IProvedorMarketing Obter(PlataformaMarketing p) => _mapa[p];
}

/// <summary>Fila em memória para sincronizações inicial/manual (o worker também varre o banco periodicamente).</summary>
public class FilaSincronizacao
{
    private readonly Channel<(int ContaId, TipoSincronizacao Tipo)> _canal =
        Channel.CreateBounded<(int, TipoSincronizacao)>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enfileirar(int contaId, TipoSincronizacao tipo) => _canal.Writer.TryWrite((contaId, tipo));
    public ChannelReader<(int ContaId, TipoSincronizacao Tipo)> Leitor => _canal.Reader;
}

/// <summary>
/// Executa a sincronização de uma conta: garante token válido, coleta da API,
/// grava histórico sem duplicar (chaves únicas por conta/data/métrica), registra log e agenda a próxima.
/// </summary>
public class SincronizacaoMarketingService(
    ApplicationDbContext db,
    RegistroProvedores provedores,
    ProtetorTokens protetor,
    ILogger<SincronizacaoMarketingService> logger)
{
    public const int DiasCargaInicial = 90;
    public const int DiasIncremental = 4;
    private static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BackoffMaximo = TimeSpan.FromHours(24);

    public async Task<HistoricoSincronizacao?> SincronizarAsync(int contaId, TipoSincronizacao tipo, CancellationToken ct)
    {
        var conta = await db.ContasMarketing.Include(c => c.Conexao).FirstOrDefaultAsync(c => c.Id == contaId, ct);
        if (conta?.Conexao == null || !conta.Ativa) return null;
        var conexao = conta.Conexao;
        if (conexao.Status is StatusIntegracao.NaoConectado or StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada)
            return null;

        var provedor = provedores.Obter(conta.Plataforma);
        var inicial = conta.UltimaSincronizacao == null;
        var hist = new HistoricoSincronizacao
        {
            ContaMarketingId = conta.Id,
            ClienteId = conta.ClienteId,
            Plataforma = conta.Plataforma,
            Tipo = inicial ? TipoSincronizacao.Inicial : tipo,
            Tentativa = conta.FalhasConsecutivas + 1
        };
        db.HistoricosSincronizacao.Add(hist);
        conta.Status = StatusIntegracao.Sincronizando;
        await db.SaveChangesAsync(ct);

        try
        {
            var token = await ObterTokenValidoAsync(conexao, provedor, ct);
            var tokenRecurso = protetor.RevelarOpcional(conta.TokenRecursoProtegido) ?? token;

            var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
            var desde = hoje.AddDays(-(inicial ? DiasCargaInicial : DiasIncremental));
            var dados = await provedor.SincronizarAsync(conta, tokenRecurso, desde, hoje, inicial, ct);

            var gravados = await GravarAsync(conta, provedor.Fonte, dados, desde, hoje, ct);

            conta.Nome = dados.Nome ?? conta.Nome;
            conta.NomeUsuario = dados.NomeUsuario ?? conta.NomeUsuario;
            conta.FotoUrl = dados.FotoUrl ?? conta.FotoUrl;
            conta.TipoConta = dados.TipoConta ?? conta.TipoConta;
            conta.MetricasIndisponiveis = dados.Indisponiveis.Count > 0 ? string.Join(",", dados.Indisponiveis.Order()) : null;
            conta.Limitacao = dados.Limitacao;
            conta.Status = StatusIntegracao.Conectado;
            conta.UltimoErro = null;
            conta.FalhasConsecutivas = 0;
            conta.UltimaSincronizacao = DateTime.UtcNow;
            conta.ProximaSincronizacao = DateTime.UtcNow.Add(MarketingOptions.IntervaloSincronizacao(conta.Plataforma));
            conexao.Status = StatusIntegracao.Conectado;
            conexao.UltimoErro = null;

            hist.Sucesso = true;
            hist.RegistrosGravados = gravados;
            hist.Mensagem = dados.Indisponiveis.Count > 0
                ? $"Métricas não disponibilizadas pela API: {string.Join(", ", dados.Indisponiveis.Select(Metricas.Nome))}."
                : null;
        }
        catch (ErroApiExterna e)
        {
            AplicarFalha(conta, conexao, hist, e.Tipo, e.Message, e.TentarApos);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            conta.Status = StatusIntegracao.Conectado;
            hist.Mensagem = "Sincronização interrompida (aplicação encerrando).";
            hist.FinalizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Falha inesperada ao sincronizar conta de marketing {ContaId}", conta.Id);
            AplicarFalha(conta, conexao, hist, TipoErroApi.Temporario, "Erro interno ao processar os dados recebidos.", null);
        }

        if (!hist.Sucesso) DescartarDadosPendentes();
        hist.FinalizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        return hist;
    }

    /// <summary>Em caso de falha no meio da gravação, não persiste dados parciais junto com o log.</summary>
    private void DescartarDadosPendentes()
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(e => e.Entity is MetricaDiaria or MetricaDimensao or PublicacaoMarketing).ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    }

    internal static void AplicarFalha(ContaMarketing conta, ConexaoMarketing conexao, HistoricoSincronizacao hist, TipoErroApi tipo, string mensagem, TimeSpan? tentarApos)
    {
        var msg = Sanitizador.Limpar(mensagem);
        hist.Sucesso = false;
        hist.Mensagem = msg;
        conta.UltimoErro = msg;
        conta.FalhasConsecutivas++;

        switch (tipo)
        {
            case TipoErroApi.TokenInvalido:
                conta.Status = conexao.Status = StatusIntegracao.TokenExpirado;
                conexao.UltimoErro = msg;
                conta.ProximaSincronizacao = null; // só volta após reconectar
                break;
            case TipoErroApi.PermissaoRevogada:
                conta.Status = conexao.Status = StatusIntegracao.PermissaoRevogada;
                conexao.UltimoErro = msg;
                conta.ProximaSincronizacao = null;
                break;
            default:
                conta.Status = StatusIntegracao.ErroSincronizacao;
                var espera = tentarApos ?? TimeSpan.FromTicks(Math.Min(
                    BackoffMaximo.Ticks, BackoffBase.Ticks * (long)Math.Pow(2, Math.Min(conta.FalhasConsecutivas - 1, 10))));
                conta.ProximaSincronizacao = DateTime.UtcNow.Add(espera);
                break;
        }
    }

    /// <summary>Renova o token quando suportado e necessário; persiste o novo token criptografado.</summary>
    public async Task<string> ObterTokenValidoAsync(ConexaoMarketing conexao, IProvedorMarketing provedor, CancellationToken ct)
    {
        var token = protetor.Revelar(conexao.TokenAcessoProtegido);
        var refresh = protetor.RevelarOpcional(conexao.TokenRefreshProtegido);

        var novo = await provedor.RenovarAsync(conexao, token, refresh, ct);
        if (novo != null)
        {
            conexao.TokenAcessoProtegido = protetor.Proteger(novo.AccessToken);
            if (novo.RefreshToken != null) conexao.TokenRefreshProtegido = protetor.Proteger(novo.RefreshToken);
            conexao.ExpiraEm = novo.ExpiraEm;
            conexao.RefreshExpiraEm = novo.RefreshExpiraEm ?? conexao.RefreshExpiraEm;
            conexao.Escopos = novo.Escopos ?? conexao.Escopos;
            conexao.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return novo.AccessToken;
        }

        if (conexao.ExpiraEm is DateTime exp && exp < DateTime.UtcNow)
            throw new ErroApiExterna(TipoErroApi.TokenInvalido, "O token de acesso expirou. Conecte a conta novamente.");
        return token;
    }

    /// <summary>Upsert de métricas diárias e publicações; substitui as quebras por dimensão no intervalo sincronizado.</summary>
    private async Task<int> GravarAsync(ContaMarketing conta, string fonte, DadosSincronizados dados, DateOnly desde, DateOnly ate, CancellationToken ct)
    {
        var agora = DateTime.UtcNow;
        var total = 0;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Métricas diárias (deduplica pela chave conta+data+métrica, prevalece o último valor coletado)
        var novas = dados.Metricas.GroupBy(m => (m.Data, m.Metrica)).ToDictionary(g => g.Key, g => g.Last().Valor);
        if (novas.Count > 0)
        {
            var min = novas.Keys.Min(k => k.Data);
            var max = novas.Keys.Max(k => k.Data);
            var existentes = await db.MetricasDiarias
                .Where(m => m.ContaMarketingId == conta.Id && m.Data >= min && m.Data <= max)
                .ToDictionaryAsync(m => (m.Data, m.Metrica), ct);
            foreach (var ((data, metrica), valor) in novas)
            {
                if (existentes.TryGetValue((data, metrica), out var e))
                {
                    e.Valor = valor;
                    e.ColetadoEm = agora;
                    e.Fonte = fonte;
                }
                else
                {
                    db.MetricasDiarias.Add(new MetricaDiaria
                    {
                        ContaMarketingId = conta.Id, ClienteId = conta.ClienteId, Plataforma = conta.Plataforma,
                        Data = data, Metrica = metrica, Valor = valor, Fonte = fonte, ColetadoEm = agora
                    });
                }
                total++;
            }
        }

        // Quebras por dimensão: substitui o intervalo para cada dimensão retornada
        foreach (var grupo in dados.Dimensoes.GroupBy(d => d.Dimensao))
        {
            var dim = grupo.Key;
            await db.MetricasDimensao
                .Where(m => m.ContaMarketingId == conta.Id && m.Dimensao == dim && m.Data >= desde && m.Data <= ate)
                .ExecuteDeleteAsync(ct);
            foreach (var d in grupo.GroupBy(x => (x.Data, x.ValorDimensao, x.Metrica)))
            {
                var item = d.Last();
                db.MetricasDimensao.Add(new MetricaDimensao
                {
                    ContaMarketingId = conta.Id, ClienteId = conta.ClienteId, Plataforma = conta.Plataforma,
                    Data = item.Data, Dimensao = dim, ValorDimensao = item.ValorDimensao, Metrica = item.Metrica,
                    Valor = item.Valor, ColetadoEm = agora
                });
                total++;
            }
        }

        // Publicações (upsert por id externo)
        if (dados.Publicacoes.Count > 0)
        {
            var ids = dados.Publicacoes.Select(p => p.IdExterno).Distinct().ToList();
            var existentes = await db.PublicacoesMarketing
                .Where(p => p.ContaMarketingId == conta.Id && ids.Contains(p.IdExterno))
                .ToDictionaryAsync(p => p.IdExterno, ct);
            foreach (var p in dados.Publicacoes.GroupBy(p => p.IdExterno).Select(g => g.Last()))
            {
                if (!existentes.TryGetValue(p.IdExterno, out var e))
                {
                    e = new PublicacaoMarketing
                    {
                        ContaMarketingId = conta.Id, ClienteId = conta.ClienteId, Plataforma = conta.Plataforma, IdExterno = p.IdExterno
                    };
                    db.PublicacoesMarketing.Add(e);
                }
                e.Tipo = p.Tipo;
                e.Legenda = p.Legenda is { Length: > 2000 } l ? l[..2000] : p.Legenda;
                e.Link = p.Link;
                e.ThumbnailUrl = p.ThumbnailUrl;
                e.PublicadoEm = p.PublicadoEm;
                e.Visualizacoes = p.Visualizacoes;
                e.Alcance = p.Alcance;
                e.Curtidas = p.Curtidas;
                e.Comentarios = p.Comentarios;
                e.Compartilhamentos = p.Compartilhamentos;
                e.Salvamentos = p.Salvamentos;
                e.Cliques = p.Cliques;
                e.Interacoes = p.Interacoes;
                e.AtualizadoEm = agora;
                total++;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return total;
    }
}

/// <summary>
/// Worker de sincronização: processa a fila (inicial/manual) e, a cada 5 minutos,
/// as contas cuja próxima sincronização venceu. Processa uma conta por vez para respeitar limites das APIs.
/// </summary>
public class SincronizacaoMarketingWorker(
    IServiceScopeFactory scopes,
    FilaSincronizacao fila,
    MarketingOptions opt,
    ILogger<SincronizacaoMarketingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Varredura = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.SincronizacaoHabilitada)
        {
            logger.LogInformation("Sincronização automática de marketing desabilitada (MARKETING_SYNC_ENABLED=false).");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        await LiberarTravadasAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Itens da fila primeiro (resposta rápida ao usuário)
                while (fila.Leitor.TryRead(out var item))
                    await ProcessarAsync(item.ContaId, item.Tipo, ct);

                await ProcessarVencidasAsync(ct);

                using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct);
                espera.CancelAfter(Varredura);
                try { await fila.Leitor.WaitToReadAsync(espera.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError(e, "Erro no ciclo de sincronização de marketing");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    private async Task ProcessarVencidasAsync(CancellationToken ct)
    {
        List<int> ids;
        using (var scope = scopes.CriarEscopoSistema())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var agora = DateTime.UtcNow;
            ids = await db.ContasMarketing
                .Where(c => c.Ativa && c.ProximaSincronizacao != null && c.ProximaSincronizacao <= agora
                            && c.Conexao!.Status != StatusIntegracao.NaoConectado
                            && c.Conexao.Status != StatusIntegracao.TokenExpirado
                            && c.Conexao.Status != StatusIntegracao.PermissaoRevogada)
                .OrderBy(c => c.ProximaSincronizacao)
                .Select(c => c.Id)
                .Take(20)
                .ToListAsync(ct);
        }
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessarAsync(id, TipoSincronizacao.Agendada, ct);
        }
    }

    private async Task ProcessarAsync(int contaId, TipoSincronizacao tipo, CancellationToken ct)
    {
        using var scope = scopes.CriarEscopoSistema();
        var servico = scope.ServiceProvider.GetRequiredService<SincronizacaoMarketingService>();
        var hist = await servico.SincronizarAsync(contaId, tipo, ct);
        if (hist != null)
            logger.LogInformation("Sincronização {Plataforma} conta {ContaId}: {Resultado}", hist.Plataforma, contaId, hist.Sucesso ? "ok" : "falhou");
    }

    /// <summary>Contas que ficaram "Sincronizando" por queda da aplicação voltam para a fila.</summary>
    private async Task LiberarTravadasAsync(CancellationToken ct)
    {
        using var scope = scopes.CriarEscopoSistema();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.ContasMarketing.Where(c => c.Status == StatusIntegracao.Sincronizando)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, StatusIntegracao.Conectado)
                .SetProperty(c => c.ProximaSincronizacao, (DateTime?)DateTime.UtcNow), ct);
    }
}
