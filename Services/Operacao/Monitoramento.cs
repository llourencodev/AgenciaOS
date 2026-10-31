using System.Collections.Concurrent;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Armazenamento;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgenciaOS.Services.Operacao;

/// <summary>Batimentos das tarefas em segundo plano (para health check e status).</summary>
public class MonitorTarefas
{
    public record Batimento(DateTime Quando, bool Sucesso, TimeSpan IntervaloEsperado, string? Erro);

    private readonly ConcurrentDictionary<string, Batimento> _ultimos = new();
    private readonly ConcurrentDictionary<string, bool> _desabilitadas = new();

    public void Registrar(string tarefa, bool sucesso, TimeSpan intervaloEsperado, string? erro = null) =>
        _ultimos[tarefa] = new Batimento(DateTime.UtcNow, sucesso, intervaloEsperado, erro is { Length: > 300 } ? erro[..300] : erro);

    public void Desabilitada(string tarefa) => _desabilitadas[tarefa] = true;

    public IReadOnlyDictionary<string, Batimento> Ultimos => _ultimos;
    public IReadOnlyCollection<string> Desabilitadas => _desabilitadas.Keys.ToList();
}

public static class TagsSaude
{
    public const string Pronto = "pronto";     // necessário para atender requisições
    public const string Status = "status";     // aparece na página pública
}

public class BancoHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        using var escopo = scopes.CriarEscopoSistema();
        var db = escopo.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var inicio = DateTime.UtcNow;
        try
        {
            if (!await db.Database.CanConnectAsync(ct)) return HealthCheckResult.Unhealthy("Sem conexão com o banco.");
            var ms = (DateTime.UtcNow - inicio).TotalMilliseconds;
            return ms > 2000 ? HealthCheckResult.Degraded($"Banco lento ({ms:0} ms).") : HealthCheckResult.Healthy($"{ms:0} ms");
        }
        catch (Exception e) { return HealthCheckResult.Unhealthy("Falha ao consultar o banco.", e); }
    }
}

public class ArmazenamentoHealthCheck(IArmazenamentoArquivos armazenamento) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            // Só leitura: confirma que o provedor responde (objeto inexistente → false, sem erro)
            await armazenamento.ExisteAsync("agencies/0/health/ping.txt", ct);
            return HealthCheckResult.Healthy(armazenamento.Nome);
        }
        catch (Exception e) { return HealthCheckResult.Unhealthy($"Armazenamento ({armazenamento.Nome}) indisponível.", e); }
    }
}

public class TarefasHealthCheck(MonitorTarefas monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var problemas = new List<string>();
        var dados = new Dictionary<string, object>();
        foreach (var (nome, b) in monitor.Ultimos)
        {
            dados[nome] = new { b.Quando, b.Sucesso };
            if (!b.Sucesso) problemas.Add($"{nome}: última execução falhou");
            else if (DateTime.UtcNow - b.Quando > b.IntervaloEsperado * 2.5) problemas.Add($"{nome}: sem execução recente");
        }
        foreach (var d in monitor.Desabilitadas) dados[d] = "desabilitada";
        return Task.FromResult(problemas.Count == 0
            ? HealthCheckResult.Healthy("Tarefas em dia", dados)
            : HealthCheckResult.Degraded(string.Join("; ", problemas), data: dados));
    }
}

/// <summary>Configuração de e-mail e pagamentos (sem chamar os fornecedores: não gasta cota nem expõe chaves).</summary>
public class IntegracoesHealthCheck(EmailOptions email, CobrancaOptions cobranca, IHostEnvironment env) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var avisos = new List<string>();
        if (env.EhProducaoOuHomologacao())
        {
            if (email.Provedor != "resend") avisos.Add("e-mail não configurado");
            if (cobranca.Gateway is "desabilitado" or "simulado") avisos.Add("pagamentos não configurados");
        }
        return Task.FromResult(avisos.Count == 0 ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded(string.Join("; ", avisos)));
    }
}

public static class AmbienteExtensions
{
    /// <summary>Produção e homologação (Staging) seguem as mesmas regras de segurança; só mudam credenciais e dados.</summary>
    public static bool EhProducaoOuHomologacao(this IHostEnvironment env) => env.IsProduction() || env.IsStaging();
}

/// <summary>
/// Retenção automática (docs/legal/POLITICA_RETENCAO.md): registros de login 6 meses, auditoria 5 anos,
/// eventos de webhook processados e histórico de sincronização 1 ano, notificações lidas 1 ano.
/// Nunca apaga dados de negócio das agências.
/// </summary>
public class RetencaoService(ApplicationDbContext db, TenantContext tenant)
{
    public record Resultado(int LoginsRemovidos, int AuditoriaRemovida, int WebhooksRemovidos, int SincronizacoesRemovidas, int NotificacoesRemovidas);

    public async Task<Resultado> ExecutarAsync(DateTime agora, CancellationToken ct = default)
    {
        tenant.UsarSistema();
        var seisMeses = agora.AddMonths(-6);
        var umAno = agora.AddYears(-1);
        var cincoAnos = agora.AddYears(-5);

        var logins = await db.RegistrosAuditoria.Where(r => r.Acao.StartsWith("conta.login") && r.Quando < seisMeses).ExecuteDeleteAsync(ct);
        var auditoria = await db.RegistrosAuditoria.Where(r => r.Quando < cincoAnos).ExecuteDeleteAsync(ct);
        var webhooks = await db.EventosWebhook.Where(e => e.ProcessadoEm != null && e.RecebidoEm < umAno).ExecuteDeleteAsync(ct);
        var sinc = await db.HistoricosSincronizacao.Where(h => h.IniciadoEm < umAno).ExecuteDeleteAsync(ct);
        var notif = await db.Notificacoes.Where(n => n.Lida && n.CriadoEm < umAno).ExecuteDeleteAsync(ct);
        return new Resultado(logins, auditoria, webhooks, sinc, notif);
    }
}

public class RetencaoWorker(IServiceScopeFactory scopes, MonitorTarefas monitor, IConfiguration cfg, ILogger<RetencaoWorker> logger) : BackgroundService
{
    public const string Nome = "retencao";
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.Equals(cfg["RETENCAO_WORKER_ENABLED"], "false", StringComparison.OrdinalIgnoreCase)) { monitor.Desabilitada(Nome); return; }
        await Task.Delay(TimeSpan.FromMinutes(2), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var escopo = scopes.CriarEscopoSistema();
                var r = await escopo.ServiceProvider.GetRequiredService<RetencaoService>().ExecutarAsync(DateTime.UtcNow, ct);
                logger.LogInformation("Retenção: {Logins} logins, {Auditoria} auditoria, {Webhooks} webhooks, {Sinc} sincronizações, {Notif} notificações removidos",
                    r.LoginsRemovidos, r.AuditoriaRemovida, r.WebhooksRemovidos, r.SincronizacoesRemovidas, r.NotificacoesRemovidas);
                monitor.Registrar(Nome, true, Intervalo);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError(e, "Erro na rotina de retenção");
                monitor.Registrar(Nome, false, Intervalo, e.Message);
            }
            await Task.Delay(Intervalo, ct);
        }
    }
}
