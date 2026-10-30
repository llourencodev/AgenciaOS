using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
using AgenciaOS.Services.Marketing;
using AgenciaOS.Services.Tenancy;
using AgenciaOS.Services.Armazenamento;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Contas;
using AgenciaOS.Services.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add<PermissaoModuloFilter>();
    o.Filters.Add<AgenciaOS.Services.Plataforma.AceiteTermosFilter>();
    o.Filters.Add<AcessoEntreAgenciasExceptionFilter>();
});
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

// Plataforma e LGPD (Fase 5)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AgenciaOS.Services.Plataforma.AuditoriaService>();
builder.Services.AddScoped<AgenciaOS.Services.Plataforma.ConfiguracaoPlataformaService>();
builder.Services.AddScoped<AgenciaOS.Services.Plataforma.MetricasPlataformaService>();
builder.Services.AddScoped<AgenciaOS.Services.Plataforma.PrivacidadeService>();

var conn = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "MISSING";

// Converte URL postgresql:// para formato chave-valor que o Npgsql aceita
static string ConverterUrlPostgres(string url)
{
    try
    {
        var uri = new Uri(url);
        var partes = uri.UserInfo.Split(':', 2);
        var user = Uri.UnescapeDataString(partes[0]);
        var pass = partes.Length > 1 ? Uri.UnescapeDataString(partes[1]) : "";
        var db   = uri.AbsolutePath.TrimStart('/');
        var port = uri.Port > 0 ? uri.Port : 5432;
        return $"Host={uri.Host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
    }
    catch { return url; }
}

bool ehPostgres = conn.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
               || conn.StartsWith("Host=",    StringComparison.OrdinalIgnoreCase);

if (ehPostgres)
{
    // Se for URL (postgresql://...), converte para key=value
    if (conn.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        conn.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        conn = ConverterUrlPostgres(conn);

    builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(conn));
}
else
{
    builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(conn));
}

builder.Services.AddIdentity<Usuario, IdentityRole>(o =>
{
    // Vale para senhas novas/alteradas; senhas existentes continuam funcionando
    o.Password.RequiredLength = 10;
    o.Password.RequireDigit = true;
    o.Password.RequireLowercase = true;
    o.Password.RequireUppercase = true;
    o.Password.RequireNonAlphanumeric = false;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.AllowedForNewUsers = true;
    o.User.RequireUniqueEmail = true;
    // Contas criadas pelo cadastro público só entram depois de confirmar o e-mail
    // (usuários existentes e convidados já estão com EmailConfirmed = true)
    o.SignIn.RequireConfirmedEmail = true;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddErrorDescriber<IdentityErrosPortugues>()
.AddDefaultTokenProviders();

// Links de confirmação e de redefinição de senha expiram em 3 horas
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(3));
// Revogação de sessões: troca de senha/desativação invalida cookies na próxima validação
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval =
    TimeSpan.FromSeconds(int.TryParse(builder.Configuration["SECURITY_STAMP_INTERVAL_SECONDS"], out var s) && s >= 0 ? s : 300));

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AcessoNegado";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddEmail(builder.Configuration);
builder.Services.AddScoped<ContasService>();
builder.Services.AddRateLimitingContas();

// Multi-tenancy: a agência vem sempre do usuário autenticado (TenantMiddleware)
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddScoped<AcessoService>();
builder.Services.AddScoped<AcessoAbas>();

builder.Services.AddScoped<NotificacaoService>();
builder.Services.AddCentralMarketing(builder.Configuration);
builder.Services.AddArmazenamento(builder.Configuration, builder.Environment);
builder.Services.AddCobranca(builder.Configuration, builder.Environment);
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AgenciaOS.Data.ApplicationDbContext>();

var app = builder.Build();

// Proxy headers (Railway / reverse proxy)
// O Railway é o único ponto de entrada; sem limpar as redes conhecidas o IP do cliente seria sempre o do proxy
// (e o rate limit por IP viraria um limite global).
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
if (app.Environment.IsProduction())
{
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
}
app.UseForwardedHeaders(forwarded);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

// Não redirecionar HTTPS no Railway (proxy cuida disso)
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

// Arquivos de clientes não são mais públicos: /uploads só é acessível via /Arquivos/Baixar (com autorização)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/uploads", StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");

// Migrations + seed (escopo em modo Sistema: roda fora de qualquer agência)
using (var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema())
    await SeedInicial.ExecutarAsync(scope.ServiceProvider, app.Environment, app.Logger);

if (app.Environment.IsProduction() && app.Services.GetRequiredService<EmailOptions>() is { } emailOpt
    && (emailOpt.Provedor != "resend" || string.IsNullOrEmpty(emailOpt.UrlBase)))
    app.Logger.LogError("E-mail não configurado para produção (EMAIL_PROVIDER=resend, RESEND_API_KEY, EMAIL_FROM, APP_BASE_URL): cadastro, convites e recuperação de senha não enviarão e-mails.");

if (app.Services.GetRequiredService<AgenciaOS.Services.Cobranca.CobrancaOptions>().Gateway == "desabilitado")
    app.Logger.LogError("Cobrança desabilitada: configure PAYMENT_GATEWAY=asaas, ASAAS_API_KEY e ASAAS_WEBHOOK_TOKEN.");

var armazenamentoOpt = app.Services.GetRequiredService<ArmazenamentoOptions>();
if (armazenamentoOpt.Provedor == "local" && app.Environment.IsProduction())
    app.Logger.LogWarning("Armazenamento local em produção ({Caminho}): sem volume persistente os arquivos se perdem a cada deploy. Configure STORAGE_PROVIDER=s3.", armazenamentoOpt.CaminhoLocal);

// Migração controlada dos arquivos legados: dotnet AgenciaOS.dll --migrar-arquivos [--remover-originais]
if (args.Contains("--migrar-arquivos"))
{
    using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CriarEscopoSistema();
    var rel = await scope.ServiceProvider.GetRequiredService<MigracaoArquivosService>()
        .ExecutarAsync(removerOriginais: args.Contains("--remover-originais"));
    Environment.ExitCode = rel.Falhas > 0 ? 1 : 0;
    return;
}

app.Run();

public partial class Program;
