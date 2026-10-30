using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AgenciaOS.Services.Cobranca;

/// <summary>
/// Limites e módulos do plano da agência atual. Sem plano (período de teste) não há limite.
/// Planos ficam em cache por 1 minuto (alterações no painel valem logo).
/// </summary>
public class LimitesPlanoService(ApplicationDbContext db, TenantContext tenant, IMemoryCache cache)
{
    public async Task<Plano?> PlanoAtualAsync(CancellationToken ct = default)
    {
        var codigo = tenant.Situacao?.PlanoCodigo;
        if (string.IsNullOrEmpty(codigo)) return null;
        return await cache.GetOrCreateAsync($"plano:{codigo}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await db.Planos.AsNoTracking().FirstOrDefaultAsync(p => p.Codigo == codigo, ct);
        });
    }

    public async Task<bool> ModuloLiberadoAsync(string aba, CancellationToken ct = default)
    {
        var plano = await PlanoAtualAsync(ct);
        return plano == null || plano.ListaModulos.Count == 0 || plano.ListaModulos.Contains(aba);
    }

    /// <summary>Mensagem de erro se o limite foi atingido; null se pode criar.</summary>
    public async Task<string?> VerificarClienteAsync(CancellationToken ct = default)
    {
        var plano = await PlanoAtualAsync(ct);
        if (plano?.LimiteClientes is not int max) return null;
        return await db.Clientes.CountAsync(c => c.Ativo, ct) >= max
            ? $"Seu plano ({plano.Nome}) permite até {max} clientes ativos. Troque de plano para cadastrar mais."
            : null;
    }

    public async Task<string?> VerificarUsuarioAsync(CancellationToken ct = default)
    {
        var plano = await PlanoAtualAsync(ct);
        if (plano?.LimiteUsuarios is not int max) return null;
        var id = tenant.AgenciaId;
        var usuarios = await db.Users.CountAsync(u => u.AgenciaId == id && u.Ativo && u.Tipo != TipoUsuario.Cliente, ct);
        var convites = await db.ConvitesUsuario.CountAsync(c => c.AceitoEm == null && c.RevogadoEm == null && c.ExpiraEm > DateTime.UtcNow && c.Tipo != TipoUsuario.Cliente, ct);
        return usuarios + convites >= max
            ? $"Seu plano ({plano.Nome}) permite até {max} usuários da equipe (convites pendentes contam). Troque de plano para adicionar mais."
            : null;
    }

    public async Task<string?> VerificarArmazenamentoAsync(long bytesNovos, CancellationToken ct = default)
    {
        var plano = await PlanoAtualAsync(ct);
        if (plano?.LimiteArmazenamentoMb is not int maxMb) return null;
        var usado = await db.Arquivos.SumAsync(a => (long?)a.TamanhoBytes, ct) ?? 0;
        return usado + bytesNovos > maxMb * 1024L * 1024L
            ? $"Seu plano ({plano.Nome}) permite {maxMb} MB de arquivos. Exclua arquivos ou troque de plano."
            : null;
    }
}
