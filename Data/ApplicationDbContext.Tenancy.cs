using System.Reflection;
using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AgenciaOS.Data;

/// <summary>
/// Isolamento entre agências: filtros globais de leitura e guarda de escrita.
/// Ver docs/architecture/MULTITENANCY_DESIGN.md.
/// </summary>
public partial class ApplicationDbContext
{
    /// <summary>Usado pelos filtros globais (o EF reavalia a cada consulta).</summary>
    public bool SemFiltroTenant => _tenant == null || _tenant.Modo == ModoTenant.Sistema;

    /// <summary>Usado pelos filtros globais. null = nenhuma linha de agência visível.</summary>
    public int? AgenciaAtualId => _tenant?.Modo == ModoTenant.Agencia ? _tenant.AgenciaId : null;

    private static readonly MethodInfo _configurarFiltro =
        typeof(ApplicationDbContext).GetMethod(nameof(ConfigurarEntidadeDaAgencia), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void ConfigurarMultiTenancy(ModelBuilder builder)
    {
        builder.Entity<Agencia>(e =>
        {
            e.Property(a => a.Nome).HasMaxLength(150);
            e.Property(a => a.NomeFantasia).HasMaxLength(150);
            e.Property(a => a.Slug).HasMaxLength(60);
            e.HasIndex(a => a.Slug).IsUnique();
            e.Property(a => a.Cnpj).HasMaxLength(18);
            e.Property(a => a.EmailPrincipal).HasMaxLength(256);
            e.Property(a => a.Telefone).HasMaxLength(30);
            e.Property(a => a.PlanoCodigo).HasMaxLength(50);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.SituacaoAssinatura).HasConversion<string>().HasMaxLength(30);
        });

        builder.Entity<Usuario>(e =>
        {
            e.HasOne(u => u.Agencia).WithMany().HasForeignKey(u => u.AgenciaId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(u => u.AgenciaId);
        });

        foreach (var tipo in builder.Model.GetEntityTypes().Select(t => t.ClrType).ToList())
            if (typeof(IPertenceAgencia).IsAssignableFrom(tipo))
                _configurarFiltro.MakeGenericMethod(tipo).Invoke(this, [builder]);

        // Entidades filhas sem coluna própria: herdam a agência do pai
        builder.Entity<ComentarioConteudo>().HasQueryFilter(c => SemFiltroTenant || c.Conteudo.AgenciaId == AgenciaAtualId);
        builder.Entity<HistoricoConteudo>().HasQueryFilter(h => SemFiltroTenant || h.Conteudo.AgenciaId == AgenciaAtualId);
        builder.Entity<MetricaDiaria>().HasQueryFilter(m => SemFiltroTenant || m.Conta!.AgenciaId == AgenciaAtualId);
        builder.Entity<MetricaDimensao>().HasQueryFilter(m => SemFiltroTenant || m.Conta!.AgenciaId == AgenciaAtualId);
        builder.Entity<PublicacaoMarketing>().HasQueryFilter(p => SemFiltroTenant || p.Conta!.AgenciaId == AgenciaAtualId);
        builder.Entity<HistoricoSincronizacao>().HasQueryFilter(h => SemFiltroTenant || h.Conta!.AgenciaId == AgenciaAtualId);
    }

    private void ConfigurarEntidadeDaAgencia<T>(ModelBuilder builder) where T : class, IPertenceAgencia
    {
        builder.Entity<T>(e =>
        {
            e.HasOne<Agencia>().WithMany().HasForeignKey(x => x.AgenciaId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AgenciaId);
            e.HasQueryFilter(x => SemFiltroTenant || x.AgenciaId == AgenciaAtualId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AplicarGuardaTenantAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await AplicarGuardaTenantAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// 1) carimba AgenciaId em registros novos; 2) recusa AgenciaId forjado ou trocado;
    /// 3) recusa alterar/excluir registro de outra agência (inclusive via Update de entidade desconectada);
    /// 4) recusa chaves estrangeiras que apontam para cliente/usuário/etc. de outra agência.
    /// </summary>
    private async Task AplicarGuardaTenantAsync(CancellationToken ct)
    {
        if (SemFiltroTenant) return;

        ChangeTracker.DetectChanges();
        var entradas = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (entradas.Count == 0) return;

        var agenciaId = AgenciaAtualId;

        foreach (var e in entradas)
        {
            switch (e.Entity)
            {
                case IPertenceAgencia p:
                    if (agenciaId is not int a)
                        throw new AcessoEntreAgenciasException($"Gravação de {e.Metadata.ClrType.Name} sem agência definida.");
                    if (e.State == EntityState.Added)
                    {
                        if (p.AgenciaId == 0) p.AgenciaId = a;
                        else if (p.AgenciaId != a) throw Negado(e);
                    }
                    else
                    {
                        VerificarDonoNaoAlterado(e, nameof(IPertenceAgencia.AgenciaId));
                        if (p.AgenciaId != a) throw Negado(e);
                    }
                    break;

                case Usuario u when agenciaId is int au:
                    if (e.State == EntityState.Added)
                    {
                        u.AgenciaId ??= au;
                        if (u.AgenciaId != au) throw Negado(e);
                    }
                    else
                    {
                        VerificarDonoNaoAlterado(e, nameof(Usuario.AgenciaId));
                        if (u.AgenciaId != au) throw Negado(e);
                    }
                    break;
            }
        }

        if (agenciaId is not int agencia) return; // modo Nenhum: só Usuario (login/lockout) chega aqui

        // Registros alterados/excluídos precisam existir na agência atual (pega Update() com Id forjado)
        foreach (var grupo in entradas
                     .Where(e => e.State is EntityState.Modified or EntityState.Deleted && e.Entity is IPertenceAgencia)
                     .GroupBy(e => e.Metadata.ClrType))
        {
            var ids = grupo.Select(e => ((IPertenceAgencia)e.Entity).Id).Distinct().ToList();
            if (await ExisteForaDaAgenciaAsync(grupo.Key, ids, agencia, ct))
                throw new AcessoEntreAgenciasException($"{grupo.Key.Name} pertence a outra agência.");
        }

        await VerificarReferenciasAsync(entradas, agencia, ct);
    }

    private static void VerificarDonoNaoAlterado(EntityEntry e, string propriedade)
    {
        var prop = e.Property(propriedade);
        if (e.State == EntityState.Modified && prop.IsModified && !Equals(prop.OriginalValue, prop.CurrentValue))
            throw new AcessoEntreAgenciasException($"Não é permitido mover {e.Metadata.ClrType.Name} para outra agência.");
    }

    private static AcessoEntreAgenciasException Negado(EntityEntry e) =>
        new($"{e.Metadata.ClrType.Name} pertence a outra agência.");

    private async Task VerificarReferenciasAsync(List<EntityEntry> entradas, int agencia, CancellationToken ct)
    {
        var pendentes = new Dictionary<Type, HashSet<object>>();

        foreach (var e in entradas.Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var fk in e.Metadata.GetForeignKeys())
            {
                var alvo = fk.PrincipalEntityType.ClrType;
                if (alvo == typeof(Agencia) || fk.Properties.Count != 1) continue;
                if (alvo != typeof(Usuario) && !typeof(IPertenceAgencia).IsAssignableFrom(alvo)) continue;

                var prop = e.Property(fk.Properties[0].Name);
                if (prop.CurrentValue is null || prop.IsTemporary) continue;
                if (e.State == EntityState.Modified && !prop.IsModified) continue;

                // Alvo sendo criado/alterado no mesmo SaveChanges: já validado pelo laço principal
                var rastreado = ChangeTracker.Entries()
                    .FirstOrDefault(x => x.Metadata.ClrType == alvo && Equals(x.Property(fk.PrincipalKey.Properties[0].Name).CurrentValue, prop.CurrentValue));
                if (rastreado is { State: EntityState.Added or EntityState.Modified }) continue;

                if (!pendentes.TryGetValue(alvo, out var valores)) pendentes[alvo] = valores = [];
                valores.Add(prop.CurrentValue);
            }
        }

        foreach (var (alvo, valores) in pendentes)
        {
            bool ok;
            if (alvo == typeof(Usuario))
            {
                var ids = valores.Cast<string>().ToList();
                ok = await Users.CountAsync(u => ids.Contains(u.Id) && u.AgenciaId == agencia, ct) == ids.Count;
            }
            else
            {
                var ids = valores.Select(Convert.ToInt32).ToList();
                ok = !await ExisteForaDaAgenciaAsync(alvo, ids, agencia, ct, exigirExistencia: true);
            }
            if (!ok) throw new AcessoEntreAgenciasException($"Referência a {alvo.Name} de outra agência (ou inexistente).");
        }
    }

    private static readonly MethodInfo _existeFora =
        typeof(ApplicationDbContext).GetMethod(nameof(ExisteForaDaAgenciaGenericoAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private Task<bool> ExisteForaDaAgenciaAsync(Type tipo, List<int> ids, int agencia, CancellationToken ct, bool exigirExistencia = false) =>
        (Task<bool>)_existeFora.MakeGenericMethod(tipo).Invoke(this, [ids, agencia, exigirExistencia, ct])!;

    private async Task<bool> ExisteForaDaAgenciaGenericoAsync<T>(List<int> ids, int agencia, bool exigirExistencia, CancellationToken ct)
        where T : class, IPertenceAgencia
    {
        var daAgencia = await Set<T>().IgnoreQueryFilters().CountAsync(x => ids.Contains(x.Id) && x.AgenciaId == agencia, ct);
        if (exigirExistencia) return daAgencia != ids.Count;
        return await Set<T>().IgnoreQueryFilters().AnyAsync(x => ids.Contains(x.Id) && x.AgenciaId != agencia, ct);
    }
}
