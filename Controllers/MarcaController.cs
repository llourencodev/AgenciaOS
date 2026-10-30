using System.Text.RegularExpressions;
using AgenciaOS.Services.Armazenamento;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgenciaOS.Controllers;

/// <summary>
/// Logos das agências (identidade visual, conteúdo público por natureza). Só serve o prefixo
/// agencies/{id}/brand/ com nomes gerados pelo sistema; nenhum outro arquivo é alcançável por aqui.
/// </summary>
[AllowAnonymous]
public partial class MarcaController(IArmazenamentoArquivos armazenamento) : Controller
{
    [GeneratedRegex(@"^logo(-marca)?-[a-f0-9]{32}\.(png|jpg|jpeg|webp)$")]
    private static partial Regex NomeValido();

    [HttpGet("/marca/{agenciaId:int}/{nome}")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Logo(int agenciaId, string nome, CancellationToken ct)
    {
        if (!NomeValido().IsMatch(nome)) return NotFound();
        var stream = await armazenamento.AbrirAsync(ChavesArmazenamento.Marca(agenciaId, nome), ct);
        if (stream == null) return NotFound();

        var mime = Path.GetExtension(nome) switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, mime);
    }
}
