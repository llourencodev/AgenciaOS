using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using AgenciaOS.Models;

namespace AgenciaOS.Controllers;

public class HomeController(IWebHostEnvironment env, ILogger<HomeController> logger) : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    /// <summary>Erro não tratado (UseExceptionHandler): mensagem em português; detalhe técnico só em Development.</summary>
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var erro = HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error;
        var modelo = new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier };
        if (erro != null)
        {
            logger.LogError(erro, "Erro não tratado (código {RequestId})", modelo.RequestId);
            if (env.IsDevelopment()) modelo.Detalhe = erro.ToString();
        }
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View(modelo);
    }

    /// <summary>Respostas de erro sem corpo (404, 403, 400...) viram uma página explicando o que houve.</summary>
    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("/Home/Status/{codigo:int}")]
    public IActionResult Status(int codigo)
    {
        var (titulo, mensagem) = ErrorViewModel.TextosPorStatus(codigo);
        return View(nameof(Error), new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            StatusCode = codigo,
            Titulo = titulo,
            Mensagem = mensagem
        });
    }
}
