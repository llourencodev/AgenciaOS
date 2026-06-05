using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

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
    o.Password.RequireDigit = false;
    o.Password.RequiredLength = 8;
    o.Password.RequireNonAlphanumeric = false;
    o.Password.RequireUppercase = false;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.AllowedForNewUsers = true;
    o.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.AccessDeniedPath = "/Account/AcessoNegado";
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
});

builder.Services.AddScoped<NotificacaoService>();

var app = builder.Build();

// Proxy headers (Railway / reverse proxy)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Não redirecionar HTTPS no Railway (proxy cuida disso)
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");

// Seed de dados iniciais
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
    var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    db.Database.Migrate();

    string[] roles = ["Admin", "Equipe", "Cliente", "ColaboradorExterno"];
    foreach (var role in roles)
        if (!await roleMgr.RoleExistsAsync(role))
            await roleMgr.CreateAsync(new IdentityRole(role));

    if (await userMgr.FindByEmailAsync("admin@agencia.com") == null)
    {
        var admin = new Usuario
        {
            UserName = "admin@agencia.com",
            Email = "admin@agencia.com",
            Nome = "Administrador",
            Tipo = TipoUsuario.Admin,
            EmailConfirmed = true
        };
        var senhaAdmin = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "Admin@2025!";
        await userMgr.CreateAsync(admin, senhaAdmin);
        await userMgr.AddToRoleAsync(admin, "Admin");
    }

    // Seed configuração inicial da agência
    if (!db.Configuracoes.Any())
    {
        db.Configuracoes.Add(new Configuracao
        {
            NomeAgencia           = "Social Unânime",
            Tagline               = "Marketing Estratégico",
            CorPrimaria           = "#732734",
            CorPrimariaDark       = "#5a1e28",
            CorSecundaria         = "#d99ec8",
            CorTextoSobrePrimaria = "#e6dace",
            CorSidebar            = "#1c0a0f",
            GradienteIcone        = "linear-gradient(135deg,#722835 0%,#9e2d45 100%)",
            GradienteSaudacao     = "linear-gradient(135deg,#4a1620 0%,#732734 60%,#9e2d45 100%)",
            LogoUrl               = "/brand/logo.png"
        });
        await db.SaveChangesAsync();
    }
}

app.Run();
