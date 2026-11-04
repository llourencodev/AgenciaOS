using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services;
using AgenciaOS.Services.Armazenamento;
using AgenciaOS.Services.Cobranca;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class ArquivosController(ApplicationDbContext db, AcessoService acesso, ServicoArquivos servico,
    ArmazenamentoOptions opcoes, IWebHostEnvironment env, ILogger<ArquivosController> logger, LimitesPlanoService limites) : Controller
{
    /// <summary>Arquivos dos clientes que o usuário enxerga (a agência já é filtrada pelo DbContext).</summary>
    private IQueryable<Arquivo> Visiveis(AcessoService.Escopo? escopo)
    {
        var clientes = acesso.Clientes(escopo).Select(c => c.Id);
        return db.Arquivos.Where(a => clientes.Contains(a.ClienteId));
    }

    public async Task<IActionResult> Index(int? clienteId, CategoriaArquivo? categoria, string? mes, string? dia)
    {
        var escopo = await acesso.ObterAsync(User);
        var query = Visiveis(escopo).Include(a => a.Cliente).Include(a => a.UploadPor).AsQueryable();

        if (clienteId.HasValue)
            query = query.Where(a => a.ClienteId == clienteId);

        if (categoria.HasValue) query = query.Where(a => a.Categoria == categoria);
        if (!string.IsNullOrEmpty(mes)) query = query.Where(a => a.MesReferencia == mes);
        if (!string.IsNullOrEmpty(dia) && DateTime.TryParse(dia, out var diaDate))
            query = query.Where(a => a.CriadoEm.Date == diaDate.Date);

        var arquivos = await query.OrderByDescending(a => a.CriadoEm).ToListAsync();

        ViewData["ClienteId"] = clienteId;
        ViewData["Clientes"] = await acesso.Clientes(escopo).Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Categoria"] = categoria;
        ViewData["Mes"] = mes;
        ViewData["Dia"] = dia;

        return View(arquivos);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(510L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 510L * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? arquivo, int clienteId, CategoriaArquivo categoria, string? mesReferencia, CancellationToken ct)
    {
        // Só envia para cliente que o usuário enxerga (cliente: o próprio; sempre dentro da agência atual)
        var escopo = await acesso.ObterAsync(User);
        var cliente = escopo == null ? null : await acesso.Clientes(escopo).FirstOrDefaultAsync(c => c.Id == clienteId, ct);
        if (cliente == null) return NotFound();

        if (arquivo == null || arquivo.Length == 0)
        {
            TempData["Erro"] = "Selecione um arquivo para enviar.";
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        if (await limites.VerificarArmazenamentoAsync(arquivo.Length, ct) is { } limite)
        {
            TempData["Erro"] = limite;
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        var r = await servico.GravarAsync(arquivo, ext => ChavesArmazenamento.ArquivoCliente(cliente.AgenciaId, cliente.Id, ext), ct: ct);
        if (r.Arquivo is not { } g)
        {
            TempData["Erro"] = r.Erro;
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        db.Arquivos.Add(new Arquivo
        {
            Nome = Path.GetFileName(g.Chave),
            NomeOriginal = g.NomeSeguro,
            Url = "",
            ChaveArmazenamento = g.Chave,
            Provedor = g.Provedor,
            HashSha256 = g.HashSha256,
            TamanhoBytes = g.Tamanho,
            TipoMime = g.Mime,
            Categoria = categoria,
            MesReferencia = mesReferencia,
            ClienteId = cliente.Id,
            UploadPorId = escopo!.Usuario.Id
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await servico.Armazenamento.ExcluirAsync(g.Chave, CancellationToken.None); // não deixa objeto órfão
            throw;
        }

        TempData["Sucesso"] = "Arquivo enviado com sucesso!";
        return RedirectToAction(nameof(Index), new { clienteId });
    }

    /// <summary>
    /// Download autenticado. Verifica dono (agência + cliente visível) e então redireciona para uma
    /// URL assinada de curta duração (S3/R2) ou transmite o arquivo (armazenamento local).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Baixar(int id, bool inline = false, CancellationToken ct = default)
    {
        var escopo = await acesso.ObterAsync(User);
        var arquivo = await Visiveis(escopo).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (arquivo == null) return NotFound();

        var mime = string.IsNullOrEmpty(arquivo.TipoMime) ? "application/octet-stream" : arquivo.TipoMime;
        inline = inline && ValidadorArquivos.PodeExibirNoNavegador(mime); // SVG/HTML nunca inline
        var nome = ValidadorArquivos.NomeSeguro(arquivo.NomeOriginal);

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'";

        if (arquivo.ChaveArmazenamento is { } chave)
        {
            var url = servico.Armazenamento.GerarUrlTemporaria(chave, opcoes.ExpiracaoUrl, nome, mime, inline);
            if (url != null) return Redirect(url);

            var stream = await servico.Armazenamento.AbrirAsync(chave, ct);
            if (stream == null)
            {
                logger.LogError("Arquivo {Id} sem objeto no armazenamento ({Chave})", arquivo.Id, chave);
                return NotFound();
            }
            Response.Headers.ContentDisposition = ValidadorArquivos.ContentDisposition(nome, inline);
            return File(stream, mime, enableRangeProcessing: true);
        }

        // Legado (antes da migração): lido do disco pela aplicação; /uploads não é mais servido como estático
        var raiz = Path.GetFullPath(Path.Combine(env.WebRootPath, "uploads")) + Path.DirectorySeparatorChar;
        var caminho = Path.GetFullPath(Path.Combine(env.WebRootPath, arquivo.Url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!caminho.StartsWith(raiz, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(caminho)) return NotFound();
        Response.Headers.ContentDisposition = ValidadorArquivos.ContentDisposition(nome, inline);
        return PhysicalFile(caminho, mime, enableRangeProcessing: true);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Excluir(int id, CancellationToken ct)
    {
        var arquivo = await db.Arquivos.FindAsync([id], ct);
        if (arquivo == null) return NotFound();

        db.Arquivos.Remove(arquivo);
        await db.SaveChangesAsync(ct);

        // Remove o objeto depois do registro: se falhar, sobra um objeto órfão (registrado em log), nunca um registro quebrado
        try
        {
            if (arquivo.ChaveArmazenamento is { } chave)
                await servico.Armazenamento.ExcluirAsync(chave, ct);
            else if (!string.IsNullOrEmpty(arquivo.Url))
            {
                var raiz = Path.GetFullPath(Path.Combine(env.WebRootPath, "uploads")) + Path.DirectorySeparatorChar;
                var caminho = Path.GetFullPath(Path.Combine(env.WebRootPath, arquivo.Url.TrimStart('/')));
                if (caminho.StartsWith(raiz, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(caminho))
                    System.IO.File.Delete(caminho);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Registro {Id} excluído, mas o objeto não pôde ser removido do armazenamento", id);
        }
        return Ok();
    }
}
