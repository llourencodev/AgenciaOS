using AgenciaOS.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Data;

public class ApplicationDbContext : IdentityDbContext<Usuario>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    // Garante que todos os DateTime salvos no PostgreSQL são UTC
    public override int SaveChanges()
    {
        NormalizarDateTimes();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        NormalizarDateTimes();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void NormalizarDateTimes()
    {
        foreach (var entry in ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var prop in entry.Properties
                .Where(p => p.CurrentValue is DateTime dt && dt.Kind == DateTimeKind.Unspecified))
            {
                prop.CurrentValue = DateTime.SpecifyKind((DateTime)prop.CurrentValue!, DateTimeKind.Utc);
            }
        }
    }

    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<Conteudo> Conteudos => Set<Conteudo>();
    public DbSet<ComentarioConteudo> ComentariosConteudo => Set<ComentarioConteudo>();
    public DbSet<HistoricoConteudo> HistoricosConteudo => Set<HistoricoConteudo>();
    public DbSet<Tarefa> Tarefas => Set<Tarefa>();
    public DbSet<Arquivo> Arquivos => Set<Arquivo>();
    public DbSet<Financeiro> Financeiros => Set<Financeiro>();
    public DbSet<Notificacao> Notificacoes => Set<Notificacao>();
    public DbSet<DataComemorativa> DatasComemorativas => Set<DataComemorativa>();
    public DbSet<Contrato> Contratos => Set<Contrato>();
    public DbSet<Configuracao> Configuracoes => Set<Configuracao>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Usuario>(e =>
        {
            e.Property(u => u.Nome).HasMaxLength(100);
            e.Property(u => u.Sobrenome).HasMaxLength(100);
        });

        builder.Entity<Conteudo>(e =>
        {
            e.HasOne(c => c.CriadoPor).WithMany().HasForeignKey(c => c.CriadoPorId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(c => c.Cliente).WithMany(cl => cl.Conteudos).HasForeignKey(c => c.ClienteId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ComentarioConteudo>(e =>
        {
            e.HasOne(c => c.Usuario).WithMany().HasForeignKey(c => c.UsuarioId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<HistoricoConteudo>(e =>
        {
            e.HasOne(h => h.Usuario).WithMany().HasForeignKey(h => h.UsuarioId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Tarefa>(e =>
        {
            e.HasOne(t => t.Responsavel).WithMany(u => u.TarefasAtribuidas).HasForeignKey(t => t.ResponsavelId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(t => t.CriadoPor).WithMany().HasForeignKey(t => t.CriadoPorId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Arquivo>(e =>
        {
            e.HasOne(a => a.UploadPor).WithMany().HasForeignKey(a => a.UploadPorId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Financeiro>(e =>
        {
            e.Property(f => f.Valor).HasPrecision(18, 2);
            e.HasOne(f => f.CriadoPor).WithMany().HasForeignKey(f => f.CriadoPorId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(f => f.Responsavel).WithMany().HasForeignKey(f => f.ResponsavelId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Cliente>(e =>
        {
            e.HasOne(c => c.UsuarioCliente).WithMany().HasForeignKey(c => c.UsuarioClienteId).OnDelete(DeleteBehavior.NoAction);
        });

        builder.Entity<Contrato>(e =>
        {
            e.HasOne(c => c.Cliente).WithMany().HasForeignKey(c => c.ClienteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.CriadoPor).WithMany().HasForeignKey(c => c.CriadoPorId).OnDelete(DeleteBehavior.NoAction);
            e.Property(c => c.Valor).HasPrecision(18, 2);
        });
    }
}
