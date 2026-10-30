using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Armazenamento;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize(Roles = "Admin")]
public class ConfiguracaoController(ApplicationDbContext db, ServicoArquivos servico) : Controller
{
    private const long MaxBytesLogo = 2 * 1024 * 1024;

    /// <summary>Configuração da agência atual (filtro global); criada com valores padrão na primeira vez.</summary>
    private async Task<Configuracao> ObterConfig()
    {
        var config = await db.Configuracoes.OrderBy(c => c.Id).FirstOrDefaultAsync();
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
        config.SidebarClara      = Request.Form.ContainsKey("chkClara");
        config.TamanhoLogo       = model.TamanhoLogo;
        config.AtualizadoEm      = DateTime.UtcNow;

        // Logos: armazenamento persistente (agencies/{id}/brand/), nome único e somente imagens raster validadas
        // pela assinatura binária (SVG permitiria XSS). Servidos por /marca/{agenciaId}/{nome}.
        foreach (var (arquivo, prefixo) in new[] { (logoFile, "logo"), (logoMarcaFile, "logo-marca") })
        {
            if (arquivo == null || arquivo.Length == 0) continue;
            string? nome = null;
            var r = await servico.GravarAsync(arquivo, ext => ChavesArmazenamento.Marca(config.AgenciaId, nome = $"{prefixo}-{Guid.NewGuid():N}{ext}"),
                ValidadorArquivos.ExtensoesImagemMarca, MaxBytesLogo);
            if (r.Arquivo == null)
            {
                TempData["Erro"] = $"Logo inválido ({r.Erro}). Use PNG, JPG ou WEBP de até 2 MB.";
                return RedirectToAction(nameof(Index));
            }

            var anterior = prefixo == "logo" ? config.LogoUrl : config.LogoMarcaUrl;
            var url = $"/marca/{config.AgenciaId}/{nome}";
            if (prefixo == "logo") config.LogoUrl = url; else config.LogoMarcaUrl = url;
            if (anterior?.StartsWith($"/marca/{config.AgenciaId}/") == true)
                await servico.Armazenamento.ExcluirAsync(ChavesArmazenamento.Marca(config.AgenciaId, anterior.Split('/').Last()));
        }

        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Configurações salvas! Recarregue a página para ver as mudanças.";
        return RedirectToAction(nameof(Index));
    }

    // Endpoint que retorna a config atual como JSON (usado pelo layout via tag helper)
    [AllowAnonymous]
    public async Task<IActionResult> Tema()
    {
        // Anônimo não tem agência: devolve a identidade padrão sem gravar nada
        var config = await db.Configuracoes.OrderBy(c => c.Id).FirstOrDefaultAsync() ?? new Configuracao();
        return Ok(new
        {
            config.NomeAgencia, config.Tagline, config.LogoUrl, config.LogoMarcaUrl,
            config.CorPrimaria, config.CorPrimariaDark, config.CorSecundaria, config.CorTextoSobrePrimaria,
            config.CorSidebar, config.GradienteIcone, config.GradienteSaudacao, config.IconesMonocromados,
            config.CorIconesInativos, config.SidebarClara, config.TamanhoLogo
        });
    }
}
