using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

var conn = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("Connection string not found.");

// Auto-detecta PostgreSQL (Railway) ou SQL Server (local)
if (conn.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || conn.StartsWith("Host=", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(conn));
else
    builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(conn));

builder.Services.AddIdentity<Usuario, IdentityRole>(o =>
{
    o.Password.RequireDigit = false;
    o.Password.RequiredLength = 6;
    o.Password.RequireNonAlphanumeric = false;
    o.Password.RequireUppercase = false;
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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("default", "{controller=Dashboard}/{action=Index}/{id?}");

// Seed de dados iniciais
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
    var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    db.Database.EnsureCreated();

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
        await userMgr.CreateAsync(admin, "Admin@123");
        await userMgr.AddToRoleAsync(admin, "Admin");
    }
}

app.Run();
