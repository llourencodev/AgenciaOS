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

        var usuario = await userManager.GetUserAsync(User);
        var ext = Path.GetExtension(arquivo.FileName);
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

        var caminho = Path.Combine(env.WebRootPath, arquivo.Url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        if (System.IO.File.Exists(caminho)) System.IO.File.Delete(caminho);

        db.Arquivos.Remove(arquivo);
        await db.SaveChangesAsync();
        return Ok();
    }
}
