using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class ArquivosController(ApplicationDbContext db, UserManager<Usuario> userManager, IWebHostEnvironment env) : Controller
{
    private static readonly HashSet<string> _extensoesPermitidas = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg",
        ".mp4", ".mov", ".avi", ".webm",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".zip", ".rar", ".7z", ".txt", ".csv"
    };

    private const long MaxBytes = 50 * 1024 * 1024; // 50 MB

    public async Task<IActionResult> Index(int? clienteId, CategoriaArquivo? categoria, string? mes, string? dia)
    {
        var usuario = await userManager.GetUserAsync(User);
        var query = db.Arquivos.Include(a => a.Cliente).Include(a => a.UploadPor).AsQueryable();

        if (usuario?.Tipo == TipoUsuario.Cliente)
        {
            var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.UsuarioClienteId == usuario.Id);
            if (cliente != null) query = query.Where(a => a.ClienteId == cliente.Id);
        }
        else if (clienteId.HasValue)
            query = query.Where(a => a.ClienteId == clienteId);

        if (categoria.HasValue) query = query.Where(a => a.Categoria == categoria);
        if (!string.IsNullOrEmpty(mes)) query = query.Where(a => a.MesReferencia == mes);
        if (!string.IsNullOrEmpty(dia) && DateTime.TryParse(dia, out var diaDate))
            query = query.Where(a => a.CriadoEm.Date == diaDate.Date);

        var arquivos = await query.OrderByDescending(a => a.CriadoEm).ToListAsync();

        ViewData["ClienteId"] = clienteId;
        ViewData["Clientes"] = await db.Clientes.Where(c => c.Ativo).OrderBy(c => c.NomeEmpresa).ToListAsync();
        ViewData["Categoria"] = categoria;
        ViewData["Mes"] = mes;
        ViewData["Dia"] = dia;

        return View(arquivos);
    }

    [HttpPost]
    public async Task<IActionResult> Upload(IFormFile arquivo, int clienteId, CategoriaArquivo categoria, string? mesReferencia)
    {
        if (arquivo == null || arquivo.Length == 0)
        {
            TempData["Erro"] = "Selecione um arquivo.";
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        if (arquivo.Length > MaxBytes)
        {
            TempData["Erro"] = "Arquivo muito grande. Limite: 50 MB.";
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        var ext = Path.GetExtension(arquivo.FileName);
        if (!_extensoesPermitidas.Contains(ext))
        {
            TempData["Erro"] = $"Tipo de arquivo não permitido: {ext}";
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        var usuario = await userManager.GetUserAsync(User);
        var nomeArquivo = $"{Guid.NewGuid()}{ext}";
        var pasta = Path.Combine(env.WebRootPath, "uploads", clienteId.ToString());
        Directory.CreateDirectory(pasta);
        var caminho = Path.Combine(pasta, nomeArquivo);

        using (var stream = new FileStream(caminho, FileMode.Create))
            await arquivo.CopyToAsync(stream);

        db.Arquivos.Add(new Arquivo
        {
            Nome = nomeArquivo,
            NomeOriginal = arquivo.FileName,
            Url = $"/uploads/{clienteId}/{nomeArquivo}",
            TamanhoBytes = arquivo.Length,
            TipoMime = arquivo.ContentType,
            Categoria = categoria,
            MesReferencia = mesReferencia,
            ClienteId = clienteId,
            UploadPorId = usuario?.Id
        });

        await db.SaveChangesAsync();
        TempData["Sucesso"] = "Arquivo enviado com sucesso!";
        return RedirectToAction(nameof(Index), new { clienteId });
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Equipe")]
    public async Task<IActionResult> Excluir(int id)
    {
        var arquivo = await db.Arquivos.FindAsync(id);
        if (arquivo == null) return NotFound();

        var uploadsRoot = Path.GetFullPath(Path.Combine(env.WebRootPath, "uploads"));
        var caminho = Path.GetFullPath(Path.Combine(env.WebRootPath, arquivo.Url.TrimStart('/')));
        if (!caminho.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase)) return BadRequest();
        if (System.IO.File.Exists(caminho)) System.IO.File.Delete(caminho);

        db.Arquivos.Remove(arquivo);
        await db.SaveChangesAsync();
        return Ok();
    }
}
