using System.Security.Claims;
using System.Text.Json;
using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AgenciaOS.Services.Plataforma;

/// <summary>
/// Trilha de auditoria. Grava na hora (SaveChanges próprio) para que a ação fique registrada mesmo se algo falhar depois.
/// Nunca registrar senhas, tokens ou conteúdo de documentos em <c>detalhes</c>.
/// </summary>
public class AuditoriaService(ApplicationDbContext db, IHttpContextAccessor http)
{
    public async Task RegistrarAsync(string acao, int? agenciaId = null, string? entidade = null, string? entidadeId = null,
        object? detalhes = null, CancellationToken ct = default)
    {
        var ctx = http.HttpContext;
        var json = detalhes == null ? null : JsonSerializer.Serialize(detalhes);
        db.RegistrosAuditoria.Add(new RegistroAuditoria
        {
            Acao = acao, AgenciaId = agenciaId, Entidade = entidade, EntidadeId = entidadeId,
            Detalhes = json is { Length: > 4000 } ? json[..4000] : json,
            UsuarioId = ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier),
            UsuarioEmail = ctx?.User.Identity?.Name,
            Ip = ctx?.Connection.RemoteIpAddress?.ToString()
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Configurações globais com cache curto. Valores padrão quando ainda não foram salvas.</summary>
public class ConfiguracaoPlataformaService(ApplicationDbContext db, IMemoryCache cache)
{
    private const string Chave = "config-plataforma";

    public async Task<ConfiguracaoPlataforma> ObterAsync(CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync(Chave, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await db.ConfiguracoesPlataforma.AsNoTracking().FirstOrDefaultAsync(c => c.Id == 1, ct) ?? new ConfiguracaoPlataforma();
        }))!;

    public async Task SalvarAsync(ConfiguracaoPlataforma nova, CancellationToken ct = default)
    {
        var atual = await db.ConfiguracoesPlataforma.FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (atual == null) { atual = new ConfiguracaoPlataforma { Id = 1 }; db.ConfiguracoesPlataforma.Add(atual); }
        atual.DiasTeste = Math.Clamp(nova.DiasTeste, 0, 90);
        atual.DiasTolerancia = Math.Clamp(nova.DiasTolerancia, 0, 60);
        atual.EmailSuporte = nova.EmailSuporte?.Trim();
        atual.EncarregadoNome = nova.EncarregadoNome?.Trim();
        atual.EncarregadoEmail = nova.EncarregadoEmail?.Trim();
        atual.ExigirAceiteTermos = nova.ExigirAceiteTermos;
        atual.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        cache.Remove(Chave);
    }
}

public record IndicadoresPlataforma(
    int TotalAgencias, int Ativas, int EmTeste, int Inadimplentes, int Suspensas, int Canceladas, int Isentas,
    int TotalUsuarios, decimal Mrr, decimal Arr, decimal TaxaCancelamento, int NovasNoPeriodo, int CanceladasNoPeriodo, DateTime Desde);

/// <summary>Indicadores do painel global. Roda em modo Sistema (o chamador garante SuperAdmin).</summary>
public class MetricasPlataformaService(ApplicationDbContext db)
{
    public async Task<IndicadoresPlataforma> CalcularAsync(DateTime agora, int dias = 30, CancellationToken ct = default)
    {
        var desde = agora.AddDays(-dias);
        var agencias = await db.Agencias.AsNoTracking()
            .Select(a => new { a.Status, a.SituacaoAssinatura, a.Isenta, a.CriadoEm }).ToListAsync(ct);

        // MRR: assinaturas vigentes que estão pagando (ativas ou em tolerância), normalizadas para mês
        var pagantes = await db.Assinaturas.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Vigente && a.CanceladaEm == null
                        && (a.Situacao == SituacaoAssinatura.Ativa || a.Situacao == SituacaoAssinatura.Tolerancia || a.Situacao == SituacaoAssinatura.Atrasada))
            .Select(a => new { a.Valor, a.Plano!.Periodicidade }).ToListAsync(ct);
        var mrr = pagantes.Sum(p => p.Periodicidade == Periodicidade.Anual ? Math.Round(p.Valor / 12m, 2) : p.Valor);

        // Churn do período: cancelamentos no período ÷ (pagantes atuais + cancelados no período)
        var canceladasPeriodo = await db.Assinaturas.IgnoreQueryFilters().CountAsync(a => a.CanceladaEm != null && a.CanceladaEm >= desde, ct);
        var baseChurn = pagantes.Count + canceladasPeriodo;

        return new IndicadoresPlataforma(
            TotalAgencias: agencias.Count,
            Ativas: agencias.Count(a => a.Status == StatusAgencia.Ativa && (a.Isenta || a.SituacaoAssinatura == SituacaoAssinatura.Ativa)),
            EmTeste: agencias.Count(a => !a.Isenta && a.SituacaoAssinatura == SituacaoAssinatura.Teste),
            Inadimplentes: agencias.Count(a => !a.Isenta && a.SituacaoAssinatura is SituacaoAssinatura.Tolerancia or SituacaoAssinatura.Atrasada or SituacaoAssinatura.PagamentoPendente),
            Suspensas: agencias.Count(a => a.Status == StatusAgencia.Suspensa || (!a.Isenta && a.SituacaoAssinatura == SituacaoAssinatura.Suspensa)),
            Canceladas: agencias.Count(a => !a.Isenta && a.SituacaoAssinatura == SituacaoAssinatura.Cancelada),
            Isentas: agencias.Count(a => a.Isenta),
            TotalUsuarios: await db.Users.CountAsync(u => u.AgenciaId != null && u.Ativo, ct),
            Mrr: mrr,
            Arr: mrr * 12,
            TaxaCancelamento: baseChurn == 0 ? 0 : Math.Round(100m * canceladasPeriodo / baseChurn, 1),
            NovasNoPeriodo: agencias.Count(a => a.CriadoEm >= desde),
            CanceladasNoPeriodo: canceladasPeriodo,
            Desde: desde);
    }
}
