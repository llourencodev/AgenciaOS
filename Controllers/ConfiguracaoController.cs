using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize(Roles = "Admin")]
public class ConfiguracaoController(ApplicationDbContext db, IWebHostEnvironment env) : Controller
{
    private async Task<Configuracao> ObterConfig()
    {
        var config = await db.Configuracoes.FirstOrDefaultAsync();
        if (config == null)
        {
            config = new Configuracao();
            db.Configuracoes.Add(config);
            await db.SaveChangesAsync();
        }
        return config;
    }

    public async Task<IActionResult> Index()
    {
        var config = await ObterConfig();
        return View(config);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Salvar(Configuracao model, IFormFile? logoFile, IFormFile? logoMarcaFile)
    {
        var config = await ObterConfig();

        config.NomeAgencia   = model.NomeAgencia;
        config.Tagline       = model.Tagline;
        config.CorPrimaria   = model.CorPrimaria;
        config.CorPrimariaDark = model.CorPrimariaDark;
        config.CorSecundaria = model.CorSecundaria;
        config.CorTextoSobrePrimaria = model.CorTextoSobrePrimaria;
        config.CorSidebar    = model.CorSidebar;
        config.GradienteIcone    = model.GradienteIcone;
        config.GradienteSaudacao = model.GradienteSaudacao;
        config.IconesMonocromados = model.IconesMonocromados;
        config.CorIconesInativos  = model.CorIconesInativos;
        config.AtualizadoEm      = DateTime.UtcNow;

        // Upload logo principal
        if (logoFile != null && logoFile.Length > 0)
        {
            var pasta = Path.Combine(env.WebRootPath, "brand");
            Directory.CreateDirectory(pasta);
            var ext = Path.GetExtension(logoFile.FileName);
            var nome = $"logo{ext}";
            var caminho = Path.Combine(pasta, nome);
            using var stream = new FileStream(caminho, FileMode.Create);
            await logoFile.CopyToAsync(stream);
            config.LogoUrl = $"/brand/{nome}";
        }

        // Upload logo marca (ícone)
        if (logoMarcaFile != null && logoMarcaFile.Length > 0)
        {
            var pasta = Path.Combine(env.WebRootPath, "brand");
            Directory.CreateDirectory(pasta);
            var ext = Path.GetExtension(logoMarcaFile.FileName);
            var nome = $"logo-marca{ext}";
            var caminho = Path.Combine(pasta, nome);
            using var stream = new FileStream(caminho, FileMode.Create);
            await logoMarcaFile.CopyToAsync(stream);
            config.LogoMarcaUrl = $"/brand/{nome}";
        }

        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Configurações salvas! Recarregue a página para ver as mudanças.";
        return RedirectToAction(nameof(Index));
    }

    // Endpoint que retorna a config atual como JSON (usado pelo layout via tag helper)
    [AllowAnonymous]
    public async Task<IActionResult> Tema()
    {
        var config = await ObterConfig();
        return Ok(config);
    }
}
