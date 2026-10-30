using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgenciaOS.Data;

public partial class ApplicationDbContext : IdentityDbContext<Usuario>, IDataProtectionKeyContext
{
    private readonly ITenantContext? _tenant;

    /// <param name="tenant">Agência atual. Sem tenant (testes antigos, dotnet ef) o contexto opera em modo Sistema.</param>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContext? tenant = null) : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Agencia> Agencias => Set<Agencia>();
    public DbSet<ConsentimentoTermos> ConsentimentosTermos => Set<ConsentimentoTermos>();
    public DbSet<ConviteUsuario> ConvitesUsuario => Set<ConviteUsuario>();
    public DbSet<Plano> Planos => Set<Plano>();
    public DbSet<Assinatura> Assinaturas => Set<Assinatura>();
    public DbSet<Pagamento> Pagamentos => Set<Pagamento>();
    public DbSet<EventoWebhook> EventosWebhook => Set<EventoWebhook>();
    public DbSet<RegistroAuditoria> RegistrosAuditoria => Set<RegistroAuditoria>();
    public DbSet<ConfiguracaoPlataforma> ConfiguracoesPlataforma => Set<ConfiguracaoPlataforma>();
    public DbSet<SolicitacaoPrivacidade> SolicitacoesPrivacidade => Set<SolicitacaoPrivacidade>();

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

    // Central de Inteligência de Marketing
    public DbSet<ConexaoMarketing> ConexoesMarketing => Set<ConexaoMarketing>();
    public DbSet<ContaMarketing> ContasMarketing => Set<ContaMarketing>();
    public DbSet<MetricaDiaria> MetricasDiarias => Set<MetricaDiaria>();
    public DbSet<MetricaDimensao> MetricasDimensao => Set<MetricaDimensao>();
    public DbSet<PublicacaoMarketing> PublicacoesMarketing => Set<PublicacaoMarketing>();
    public DbSet<HistoricoSincronizacao> HistoricosSincronizacao => Set<HistoricoSincronizacao>();
    public DbSet<RelatorioMarketing> RelatoriosMarketing => Set<RelatorioMarketing>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Converte todos os DateTime para UTC antes de salvar no PostgreSQL
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        var utcNullableConverter = new ValueConverter<DateTime?, DateTime?>(
            v => v == null ? null : v.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)
                : v.Value.ToUniversalTime(),
            v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(utcConverter);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(utcNullableConverter);
            }
        }

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
            e.Property(a => a.ChaveArmazenamento).HasMaxLength(500);
            e.Property(a => a.Provedor).HasMaxLength(20);
            e.Property(a => a.HashSha256).HasMaxLength(64);
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

        // ── Central de Inteligência de Marketing ──────────────────────────
        builder.Entity<ConexaoMarketing>(e =>
        {
            e.HasOne(c => c.Cliente).WithMany().HasForeignKey(c => c.ClienteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.ConectadoPor).WithMany().HasForeignKey(c => c.ConectadoPorId).OnDelete(DeleteBehavior.SetNull);
            e.Property(c => c.Plataforma).HasConversion<string>().HasMaxLength(30);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(c => new { c.ClienteId, c.Plataforma }).IsUnique();
        });

        builder.Entity<ContaMarketing>(e =>
        {
            e.HasOne(c => c.Cliente).WithMany().HasForeignKey(c => c.ClienteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Conexao).WithMany(x => x.Contas).HasForeignKey(c => c.ConexaoMarketingId).OnDelete(DeleteBehavior.Cascade);
            e.Property(c => c.Plataforma).HasConversion<string>().HasMaxLength(30);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(c => c.IdExterno).HasMaxLength(300);
            e.HasIndex(c => new { c.ClienteId, c.Plataforma, c.IdExterno }).IsUnique();
            e.HasIndex(c => new { c.Ativa, c.ProximaSincronizacao });
        });

        builder.Entity<MetricaDiaria>(e =>
        {
            e.HasOne(m => m.Conta).WithMany(c => c.Metricas).HasForeignKey(m => m.ContaMarketingId).OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Plataforma).HasConversion<string>().HasMaxLength(30);
            e.Property(m => m.Metrica).HasMaxLength(60);
            e.Property(m => m.Fonte).HasMaxLength(120);
            e.Property(m => m.Valor).HasPrecision(20, 4);
            e.HasIndex(m => new { m.ContaMarketingId, m.Data, m.Metrica }).IsUnique();
            e.HasIndex(m => new { m.ClienteId, m.Plataforma, m.Data });
        });

        builder.Entity<MetricaDimensao>(e =>
        {
            e.HasOne(m => m.Conta).WithMany().HasForeignKey(m => m.ContaMarketingId).OnDelete(DeleteBehavior.Cascade);
            e.Property(m => m.Plataforma).HasConversion<string>().HasMaxLength(30);
            e.Property(m => m.Dimensao).HasMaxLength(60);
            e.Property(m => m.ValorDimensao).HasMaxLength(500);
            e.Property(m => m.Metrica).HasMaxLength(60);
            e.Property(m => m.Valor).HasPrecision(20, 4);
            e.HasIndex(m => new { m.ContaMarketingId, m.Data, m.Dimensao, m.ValorDimensao, m.Metrica }).IsUnique();
            e.HasIndex(m => new { m.ClienteId, m.Dimensao, m.Data });
        });

        builder.Entity<PublicacaoMarketing>(e =>
        {
            e.HasOne(p => p.Conta).WithMany(c => c.Publicacoes).HasForeignKey(p => p.ContaMarketingId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.Plataforma).HasConversion<string>().HasMaxLength(30);
            e.Property(p => p.IdExterno).HasMaxLength(200);
            e.HasIndex(p => new { p.ContaMarketingId, p.IdExterno }).IsUnique();
            e.HasIndex(p => new { p.ClienteId, p.PublicadoEm });
        });

        builder.Entity<HistoricoSincronizacao>(e =>
        {
            e.HasOne(h => h.Conta).WithMany().HasForeignKey(h => h.ContaMarketingId).OnDelete(DeleteBehavior.Cascade);
            e.Property(h => h.Plataforma).HasConversion<string>().HasMaxLength(30);
            e.Property(h => h.Tipo).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.Mensagem).HasMaxLength(2000);
            e.HasIndex(h => new { h.ClienteId, h.IniciadoEm });
        });

        builder.Entity<RelatorioMarketing>(e =>
        {
            e.HasOne(r => r.Cliente).WithMany().HasForeignKey(r => r.ClienteId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.GeradoPor).WithMany().HasForeignKey(r => r.GeradoPorId).OnDelete(DeleteBehavior.SetNull);
            e.Property(r => r.Titulo).HasMaxLength(200);
        });

        // ── Contas (Fase 3) ───────────────────────────────────────────────
        builder.Entity<ConsentimentoTermos>(e =>
        {
            e.HasOne(c => c.Usuario).WithMany().HasForeignKey(c => c.UsuarioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Agencia>().WithMany().HasForeignKey(c => c.AgenciaId).OnDelete(DeleteBehavior.Restrict);
            e.Property(c => c.Documento).HasMaxLength(60);
            e.Property(c => c.Versao).HasMaxLength(30);
            e.Property(c => c.Ip).HasMaxLength(64);
            e.Property(c => c.UserAgent).HasMaxLength(400);
            e.HasIndex(c => new { c.UsuarioId, c.Documento });
        });

        builder.Entity<ConviteUsuario>(e =>
        {
            e.HasOne(c => c.CriadoPor).WithMany().HasForeignKey(c => c.CriadoPorId).OnDelete(DeleteBehavior.SetNull);
            e.Property(c => c.Email).HasMaxLength(256);
            e.Property(c => c.Nome).HasMaxLength(100);
            e.Property(c => c.TokenHash).HasMaxLength(64);
            e.HasIndex(c => c.TokenHash).IsUnique();
        });

        // ── Planos e cobrança (Fase 4) ───────────────────────────────────
        builder.Entity<Plano>(e =>
        {
            e.Property(p => p.Codigo).HasMaxLength(50);
            e.HasIndex(p => p.Codigo).IsUnique();
            e.Property(p => p.Nome).HasMaxLength(100);
            e.Property(p => p.Descricao).HasMaxLength(1000);
            e.Property(p => p.Preco).HasPrecision(18, 2);
            e.Property(p => p.Periodicidade).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Modulos).HasMaxLength(500);
            e.Property(p => p.RecursosAdicionais).HasMaxLength(2000);
        });

        builder.Entity<Assinatura>(e =>
        {
            e.HasOne(a => a.Plano).WithMany().HasForeignKey(a => a.PlanoId).OnDelete(DeleteBehavior.Restrict);
            e.Property(a => a.Situacao).HasConversion<string>().HasMaxLength(30);
            e.Property(a => a.FormaPagamento).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Valor).HasPrecision(18, 2);
            e.Property(a => a.Gateway).HasMaxLength(20);
            e.Property(a => a.GatewayClienteId).HasMaxLength(100);
            e.Property(a => a.GatewayAssinaturaId).HasMaxLength(100);
            e.HasIndex(a => new { a.Gateway, a.GatewayAssinaturaId }).IsUnique();
            e.HasIndex(a => new { a.AgenciaId, a.Vigente });
        });

        builder.Entity<Pagamento>(e =>
        {
            e.HasOne(p => p.Assinatura).WithMany(a => a.Pagamentos).HasForeignKey(p => p.AssinaturaId).OnDelete(DeleteBehavior.Restrict);
            e.Property(p => p.GatewayPagamentoId).HasMaxLength(100);
            e.HasIndex(p => p.GatewayPagamentoId).IsUnique();
            e.Property(p => p.Valor).HasPrecision(18, 2);
            e.Property(p => p.ValorLiquido).HasPrecision(18, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.FormaPagamento).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.UrlFatura).HasMaxLength(500);
        });

        builder.Entity<EventoWebhook>(e =>
        {
            e.Property(w => w.Gateway).HasMaxLength(20);
            e.Property(w => w.EventoId).HasMaxLength(100);
            e.Property(w => w.Tipo).HasMaxLength(80);
            e.Property(w => w.Erro).HasMaxLength(2000);
            e.HasIndex(w => new { w.Gateway, w.EventoId }).IsUnique();
        });

        // ── Plataforma e LGPD (Fase 5) ───────────────────────────────────
        builder.Entity<RegistroAuditoria>(e =>
        {
            e.Property(r => r.UsuarioId).HasMaxLength(450);
            e.Property(r => r.UsuarioEmail).HasMaxLength(256);
            e.Property(r => r.Acao).HasMaxLength(80);
            e.Property(r => r.Entidade).HasMaxLength(60);
            e.Property(r => r.EntidadeId).HasMaxLength(100);
            e.Property(r => r.Detalhes).HasMaxLength(4000);
            e.Property(r => r.Ip).HasMaxLength(64);
            e.HasIndex(r => r.Quando);
            e.HasIndex(r => new { r.AgenciaId, r.Quando });
        });

        builder.Entity<ConfiguracaoPlataforma>(e =>
        {
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.EmailSuporte).HasMaxLength(256);
            e.Property(c => c.EncarregadoNome).HasMaxLength(150);
            e.Property(c => c.EncarregadoEmail).HasMaxLength(256);
        });

        builder.Entity<SolicitacaoPrivacidade>(e =>
        {
            e.Property(s => s.Protocolo).HasMaxLength(20);
            e.HasIndex(s => s.Protocolo).IsUnique();
            e.HasOne<Agencia>().WithMany().HasForeignKey(s => s.AgenciaId).OnDelete(DeleteBehavior.Restrict);
            e.Property(s => s.UsuarioId).HasMaxLength(450);
            e.Property(s => s.NomeTitular).HasMaxLength(150);
            e.Property(s => s.EmailTitular).HasMaxLength(256);
            e.Property(s => s.Descricao).HasMaxLength(4000);
            e.Property(s => s.Resposta).HasMaxLength(4000);
            e.Property(s => s.Tipo).HasConversion<string>().HasMaxLength(40);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(s => s.Ip).HasMaxLength(64);
            e.HasIndex(s => new { s.Status, s.PrazoEm });
        });

        builder.Entity<Agencia>().Property(a => a.MotivoSuspensao).HasMaxLength(500);

        ConfigurarMultiTenancy(builder);
    }
}
