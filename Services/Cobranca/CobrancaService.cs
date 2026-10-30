using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Cobranca;

/// <summary>
/// Ciclo de vida da assinatura de uma agência.
///
/// Regras (situação em Agencia.SituacaoAssinatura, consultada a cada requisição):
/// - Teste: acesso total até TesteExpiraEm; depois, Suspensa se não houver pagamento.
/// - PagamentoPendente: assinou e ainda não pagou a primeira cobrança. Acesso mantido enquanto o teste valer;
///   sem teste vigente, o acesso fica bloqueado até o pagamento (ver <see cref="SituacoesBloqueadas"/>).
/// - Ativa: pagamento confirmado pelo gateway (webhook).
/// - Tolerancia: cobrança venceu; acesso mantido por N dias (BILLING_TOLERANCIA_DIAS) com aviso.
/// - Suspensa: tolerância ou teste acabou. Só a tela de assinatura fica acessível. Nenhum dado é apagado.
/// - Cancelada: cancelamento pedido; acesso até o fim do período pago, depois bloqueio. Dados preservados.
/// O bloqueio é por agência: nenhuma outra é afetada.
/// </summary>
public class CobrancaService(
    ApplicationDbContext db,
    IGatewayPagamento gateway,
    CobrancaOptions opt,
    TenantContext tenant,
    IEnviadorEmail email,
    ILogger<CobrancaService> logger)
{
    public static readonly SituacaoAssinatura[] SituacoesBloqueadas = [SituacaoAssinatura.Suspensa, SituacaoAssinatura.Cancelada];

    public static bool Bloqueada(SituacaoAssinatura s, DateTime? testeExpiraEm, DateTime agora) =>
        SituacoesBloqueadas.Contains(s)
        || (s == SituacaoAssinatura.Teste && testeExpiraEm is { } fim && fim < agora)
        || (s == SituacaoAssinatura.PagamentoPendente && (testeExpiraEm is not { } fimTeste || fimTeste < agora));

    public Task<Assinatura?> VigenteAsync(int agenciaId, CancellationToken ct = default) =>
        db.Assinaturas.IgnoreQueryFilters().Include(a => a.Plano)
            .Where(a => a.AgenciaId == agenciaId && a.Vigente)
            .OrderByDescending(a => a.Id).FirstOrDefaultAsync(ct);

    // ── Contratação (chamado pelo Admin da agência) ──────────────────────
    public async Task<string> AssinarAsync(int agenciaId, int planoId, FormaPagamento forma, string cpfCnpj, CancellationToken ct = default)
    {
        var agencia = await db.Agencias.SingleAsync(a => a.Id == agenciaId, ct);
        var plano = await db.Planos.SingleOrDefaultAsync(p => p.Id == planoId && p.Ativo, ct)
                    ?? throw new ErroGateway("Plano indisponível.");
        var digitos = GatewayAsaas.SoDigitos(cpfCnpj ?? "");
        if (digitos.Length is not (11 or 14)) throw new ErroGateway("Informe um CPF ou CNPJ válido para a cobrança.");

        var atual = await VigenteAsync(agenciaId, ct);
        if (atual is { Situacao: SituacaoAssinatura.Ativa or SituacaoAssinatura.Tolerancia, CanceladaEm: null })
            throw new ErroGateway("A agência já tem uma assinatura ativa. Use a troca de plano.");

        // Assinatura anterior (pendente/cancelada) deixa de valer; no gateway é cancelada para não gerar novas cobranças
        if (atual != null)
        {
            if (atual.GatewayAssinaturaId != null && atual.CanceladaEm == null)
                await gateway.CancelarAssinaturaAsync(atual.GatewayAssinaturaId, ct);
            atual.Vigente = false;
            atual.AtualizadoEm = DateTime.UtcNow;
        }

        var emailCobranca = agencia.EmailPrincipal ?? await EmailAdminAsync(agenciaId, ct) ?? throw new ErroGateway("Agência sem e-mail.");
        var clienteId = atual?.GatewayClienteId ?? await gateway.CriarClienteAsync(
            new ClienteGateway(agencia.Nome, emailCobranca, digitos, agencia.Telefone, $"agencia-{agencia.Id}"), ct);

        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);
        var gatewayId = await gateway.CriarAssinaturaAsync(new NovaAssinaturaGateway(
            clienteId, forma, plano.Preco, hoje, plano.Periodicidade, $"AgenciaOS - {plano.Nome}", $"agencia-{agencia.Id}"), ct);

        var assinatura = new Assinatura
        {
            AgenciaId = agencia.Id, PlanoId = plano.Id, Situacao = SituacaoAssinatura.PagamentoPendente,
            FormaPagamento = forma, Valor = plano.Preco, Gateway = gateway.Nome,
            GatewayClienteId = clienteId, GatewayAssinaturaId = gatewayId, ProximoVencimento = hoje.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
        };
        db.Assinaturas.Add(assinatura);
        if (string.IsNullOrEmpty(agencia.Cnpj) && digitos.Length == 14) agencia.Cnpj = digitos;
        agencia.PlanoCodigo = plano.Codigo;
        // Em teste, continua em teste até o pagamento; fora dele, aguarda o pagamento
        if (agencia.SituacaoAssinatura != SituacaoAssinatura.Teste) agencia.SituacaoAssinatura = SituacaoAssinatura.PagamentoPendente;
        agencia.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Link da primeira cobrança (checkout hospedado pelo gateway: PIX, boleto ou cartão)
        var cobrancas = await gateway.ListarCobrancasAsync(gatewayId, ct);
        var primeira = cobrancas.OrderBy(c => c.Vencimento).FirstOrDefault();
        if (primeira != null)
        {
            await RegistrarCobrancaAsync(assinatura, primeira.Id, primeira.Valor, null, primeira.Status, forma, primeira.Vencimento, null, primeira.UrlFatura, ct);
            await db.SaveChangesAsync(ct);
        }
        return primeira?.UrlFatura ?? "/Assinatura";
    }

    public async Task AlterarPlanoAsync(int agenciaId, int planoId, CancellationToken ct = default)
    {
        var atual = await VigenteAsync(agenciaId, ct) ?? throw new ErroGateway("Nenhuma assinatura para alterar.");
        var plano = await db.Planos.SingleOrDefaultAsync(p => p.Id == planoId && p.Ativo, ct) ?? throw new ErroGateway("Plano indisponível.");
        if (plano.Periodicidade != atual.Plano!.Periodicidade)
            throw new ErroGateway("Para mudar entre mensal e anual, cancele e assine o novo plano.");

        await gateway.AtualizarValorAsync(atual.GatewayAssinaturaId!, plano.Preco, $"AgenciaOS - {plano.Nome}", ct);
        atual.PlanoId = plano.Id;
        atual.Valor = plano.Preco;
        atual.AtualizadoEm = DateTime.UtcNow;
        var agencia = await db.Agencias.SingleAsync(a => a.Id == agenciaId, ct);
        agencia.PlanoCodigo = plano.Codigo;
        agencia.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Cancela no gateway (sem novas cobranças). O acesso segue até o fim do período já pago.</summary>
    public async Task CancelarAsync(int agenciaId, CancellationToken ct = default)
    {
        var atual = await VigenteAsync(agenciaId, ct) ?? throw new ErroGateway("Nenhuma assinatura para cancelar.");
        if (atual.CanceladaEm != null) return;
        if (atual.GatewayAssinaturaId != null) await gateway.CancelarAssinaturaAsync(atual.GatewayAssinaturaId, ct);

        var agora = DateTime.UtcNow;
        var pagoAte = await PeriodoPagoAteAsync(atual, ct);
        atual.CanceladaEm = agora;
        atual.AcessoAte = pagoAte > agora ? pagoAte : agora;
        atual.AtualizadoEm = agora;
        if (atual.AcessoAte <= agora) await MudarSituacaoAsync(atual, SituacaoAssinatura.Cancelada, ct);
        await db.SaveChangesAsync(ct);
        await AvisarAsync(atual.AgenciaId, "Assinatura cancelada",
            $"A assinatura foi cancelada. O acesso continua até {atual.AcessoAte:dd/MM/yyyy}. Os dados ficam preservados e você pode reativar escolhendo um plano.", ct);
    }

    private async Task<DateTime> PeriodoPagoAteAsync(Assinatura a, CancellationToken ct)
    {
        var ultimoPago = await db.Pagamentos.IgnoreQueryFilters()
            .Where(p => p.AssinaturaId == a.Id && (p.Status == StatusPagamento.Confirmado || p.Status == StatusPagamento.Recebido))
            .OrderByDescending(p => p.Vencimento).Select(p => p.Vencimento).FirstOrDefaultAsync(ct);
        if (ultimoPago is not { } venc) return DateTime.MinValue;
        return a.Plano!.Periodicidade == Periodicidade.Anual ? venc.AddYears(1) : venc.AddMonths(1);
    }

    // ── Webhooks ─────────────────────────────────────────────────────────
    public enum ResultadoWebhook { Processado, Duplicado, Ignorado }

    /// <summary>
    /// Idempotente: o mesmo evento (gateway + id) só produz efeito uma vez. Eventos que falharam são reprocessados
    /// quando o gateway reenviar. Roda em modo Sistema (o webhook não pertence a um usuário).
    /// </summary>
    public async Task<ResultadoWebhook> ProcessarWebhookAsync(string nomeGateway, string payload, EventoPagamento? ev,
        string eventoId, string tipo, CancellationToken ct = default)
    {
        tenant.UsarSistema();

        var registro = await db.EventosWebhook.FirstOrDefaultAsync(e => e.Gateway == nomeGateway && e.EventoId == eventoId, ct);
        if (registro?.ProcessadoEm != null) return ResultadoWebhook.Duplicado;
        if (registro == null)
        {
            registro = new EventoWebhook { Gateway = nomeGateway, EventoId = eventoId, Tipo = tipo, Payload = payload };
            db.EventosWebhook.Add(registro);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // Entrega simultânea do mesmo evento: a outra requisição processa
                db.ChangeTracker.Clear();
                return ResultadoWebhook.Duplicado;
            }
        }
        registro.Tentativas++;

        try
        {
            var resultado = ev == null ? ResultadoWebhook.Ignorado : await AplicarEventoAsync(ev, ct);
            registro.ProcessadoEm = DateTime.UtcNow;
            registro.Erro = resultado == ResultadoWebhook.Ignorado ? "ignorado: sem assinatura correspondente ou tipo não tratado" : null;
            await db.SaveChangesAsync(ct);
            return resultado;
        }
        catch (Exception e)
        {
            db.ChangeTracker.Clear();
            var r = await db.EventosWebhook.SingleAsync(x => x.Id == registro.Id, ct);
            r.Tentativas = registro.Tentativas;
            r.Erro = e.Message.Length > 2000 ? e.Message[..2000] : e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ResultadoWebhook> AplicarEventoAsync(EventoPagamento ev, CancellationToken ct)
    {
        if (ev.AssinaturaId == null) return ResultadoWebhook.Ignorado;
        var assinatura = await db.Assinaturas.Include(a => a.Plano)
            .FirstOrDefaultAsync(a => a.Gateway == gateway.Nome && a.GatewayAssinaturaId == ev.AssinaturaId, ct);
        if (assinatura == null)
        {
            logger.LogWarning("Webhook {Evento} para assinatura desconhecida {Assinatura}", ev.Tipo, ev.AssinaturaId);
            return ResultadoWebhook.Ignorado;
        }

        var pagamento = await RegistrarCobrancaAsync(assinatura, ev.PagamentoId, ev.Valor, ev.ValorLiquido, ev.Status, ev.Forma, ev.Vencimento, ev.PagoEm, ev.UrlFatura, ct);
        var agora = DateTime.UtcNow;

        switch (pagamento.Status)
        {
            case StatusPagamento.Confirmado or StatusPagamento.Recebido:
                assinatura.ToleranciaAte = null;
                if (ev.Vencimento is { } venc)
                {
                    var proximo = assinatura.Plano!.Periodicidade == Periodicidade.Anual ? venc.AddYears(1) : venc.AddMonths(1);
                    assinatura.ProximoVencimento = proximo.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                }
                if (assinatura.CanceladaEm == null && assinatura.Situacao != SituacaoAssinatura.Ativa)
                {
                    await MudarSituacaoAsync(assinatura, SituacaoAssinatura.Ativa, ct);
                    await AvisarAsync(assinatura.AgenciaId, "Pagamento confirmado",
                        $"Recebemos o pagamento de {pagamento.Valor:C} referente ao plano {assinatura.Plano!.Nome}. Obrigado!", ct);
                }
                break;

            case StatusPagamento.Vencido:
                // Cobrança mais antiga ainda aberta manda; atraso de uma cobrança já quitada não vale (eventos fora de ordem)
                if (assinatura.Situacao is SituacaoAssinatura.Ativa or SituacaoAssinatura.PagamentoPendente or SituacaoAssinatura.Teste)
                {
                    assinatura.ToleranciaAte = agora.AddDays(opt.DiasTolerancia);
                    await MudarSituacaoAsync(assinatura, SituacaoAssinatura.Tolerancia, ct);
                    await AvisarAsync(assinatura.AgenciaId, "Pagamento em atraso",
                        $"A cobrança de {pagamento.Valor:C} venceu. Regularize até {assinatura.ToleranciaAte:dd/MM/yyyy} para evitar a suspensão do acesso. Os dados não serão apagados.",
                        ct, pagamento.UrlFatura);
                }
                break;

            case StatusPagamento.Estornado or StatusPagamento.Contestado:
                if (assinatura.Situacao == SituacaoAssinatura.Ativa)
                {
                    assinatura.ToleranciaAte = agora.AddDays(opt.DiasTolerancia);
                    await MudarSituacaoAsync(assinatura, SituacaoAssinatura.Atrasada, ct);
                }
                logger.LogWarning("Pagamento {Pagamento} da agência {Agencia} estornado/contestado ({Tipo})", pagamento.GatewayPagamentoId, assinatura.AgenciaId, ev.Tipo);
                break;
        }

        assinatura.AtualizadoEm = agora;
        return ResultadoWebhook.Processado;
    }

    private async Task<Pagamento> RegistrarCobrancaAsync(Assinatura a, string gatewayId, decimal valor, decimal? liquido, StatusPagamento status,
        FormaPagamento forma, DateOnly? vencimento, DateTime? pagoEm, string? url, CancellationToken ct)
    {
        var p = db.Pagamentos.Local.FirstOrDefault(x => x.GatewayPagamentoId == gatewayId)
                ?? await db.Pagamentos.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.GatewayPagamentoId == gatewayId, ct);
        if (p == null)
        {
            p = new Pagamento { AgenciaId = a.AgenciaId, Assinatura = a, AssinaturaId = a.Id, GatewayPagamentoId = gatewayId };
            db.Pagamentos.Add(p);
        }
        else if (p.AssinaturaId != a.Id) throw new InvalidOperationException("Pagamento pertence a outra assinatura.");

        // Uma cobrança quitada não volta para pendente/vencida por evento atrasado
        if (!(p.Quitado && status is StatusPagamento.Pendente or StatusPagamento.Vencido))
            p.Status = status;
        p.Valor = valor;
        p.ValorLiquido = liquido ?? p.ValorLiquido;
        p.FormaPagamento = forma == FormaPagamento.Indefinida ? p.FormaPagamento : forma;
        p.Vencimento = vencimento?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ?? p.Vencimento;
        p.PagoEm = pagoEm ?? (p.Quitado ? p.PagoEm ?? DateTime.UtcNow : p.PagoEm);
        p.UrlFatura = url ?? p.UrlFatura;
        p.AtualizadoEm = DateTime.UtcNow;
        return p;
    }

    // ── Rotina periódica (worker) ────────────────────────────────────────
    /// <summary>Teste vencido, tolerância esgotada e cancelamentos no fim do período viram bloqueio. Modo Sistema.</summary>
    public async Task<int> AtualizarSituacoesAsync(DateTime agora, CancellationToken ct = default)
    {
        tenant.UsarSistema();
        var mudancas = 0;

        var testesVencidos = await db.Agencias
            .Where(a => a.SituacaoAssinatura == SituacaoAssinatura.Teste && a.TesteExpiraEm != null && a.TesteExpiraEm < agora)
            .ToListAsync(ct);
        foreach (var ag in testesVencidos)
        {
            ag.SituacaoAssinatura = SituacaoAssinatura.Suspensa;
            ag.AtualizadoEm = agora;
            mudancas++;
            await AvisarAsync(ag.Id, "Período de teste encerrado",
                "O período de teste terminou e o acesso foi suspenso. Escolha um plano para continuar; todos os dados foram preservados.", ct);
        }

        var assinaturas = await db.Assinaturas.Include(a => a.Plano).Where(a => a.Vigente).ToListAsync(ct);
        foreach (var a in assinaturas)
        {
            if (a.Situacao is SituacaoAssinatura.Tolerancia or SituacaoAssinatura.Atrasada && a.ToleranciaAte < agora)
            {
                await MudarSituacaoAsync(a, SituacaoAssinatura.Suspensa, ct);
                mudancas++;
                await AvisarAsync(a.AgenciaId, "Acesso suspenso",
                    "A tolerância para o pagamento terminou e o acesso foi suspenso. Os dados estão preservados; regularize o pagamento para reativar.", ct);
            }
            else if (a.CanceladaEm != null && a.AcessoAte < agora && a.Situacao != SituacaoAssinatura.Cancelada)
            {
                await MudarSituacaoAsync(a, SituacaoAssinatura.Cancelada, ct);
                mudancas++;
            }
        }

        await db.SaveChangesAsync(ct);
        return mudancas;
    }

    private async Task MudarSituacaoAsync(Assinatura a, SituacaoAssinatura nova, CancellationToken ct)
    {
        if (a.Situacao != nova)
            logger.LogInformation("Assinatura {Id} da agência {Agencia}: {De} → {Para}", a.Id, a.AgenciaId, a.Situacao, nova);
        a.Situacao = nova;
        a.AtualizadoEm = DateTime.UtcNow;
        var agencia = await db.Agencias.SingleAsync(x => x.Id == a.AgenciaId, ct);
        agencia.SituacaoAssinatura = nova;
        agencia.AtualizadoEm = DateTime.UtcNow;
    }

    private async Task<string?> EmailAdminAsync(int agenciaId, CancellationToken ct) =>
        await db.Users.Where(u => u.AgenciaId == agenciaId && u.Tipo == TipoUsuario.Admin && u.Ativo && u.EmailConfirmed)
            .OrderBy(u => u.CriadoEm).Select(u => u.Email).FirstOrDefaultAsync(ct);

    /// <summary>Aviso ao responsável pela agência. Falha de e-mail nunca interrompe a cobrança.</summary>
    private async Task AvisarAsync(int agenciaId, string assunto, string mensagem, CancellationToken ct, string? link = null)
    {
        try
        {
            var destino = await db.Agencias.Where(a => a.Id == agenciaId).Select(a => a.EmailPrincipal).FirstOrDefaultAsync(ct)
                          ?? await EmailAdminAsync(agenciaId, ct);
            if (destino == null) return;
            await email.EnviarAsync(TemplatesEmail.Montar(destino, $"AgenciaOS: {assunto}", assunto,
                System.Text.Encodings.Web.HtmlEncoder.Default.Encode(mensagem),
                link == null ? null : "Ver cobrança", link, categoria: "cobranca"), ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Falha ao enviar aviso de cobrança para a agência {Agencia}", agenciaId);
        }
    }
}

/// <summary>Atualiza situações vencidas a cada hora (teste, tolerância, cancelamentos).</summary>
public class AssinaturasWorker(IServiceScopeFactory scopes, CobrancaOptions opt, ILogger<AssinaturasWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.WorkerHabilitado) return;
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var escopo = scopes.CriarEscopoSistema();
                var n = await escopo.ServiceProvider.GetRequiredService<CobrancaService>().AtualizarSituacoesAsync(DateTime.UtcNow, ct);
                if (n > 0) logger.LogInformation("Rotina de assinaturas: {N} mudanças de situação", n);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError(e, "Erro na rotina de assinaturas"); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}
