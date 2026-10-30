using System.Security.Claims;
using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services;

/// <summary>
/// O que o usuário pode ver DENTRO da agência (o isolamento entre agências é feito pelo DbContext).
/// - Admin/Equipe: todos os clientes da agência.
/// - Cliente: somente o cliente vinculado a ele (Clientes.UsuarioClienteId). Sem vínculo, nada.
/// - Colaborador externo: clientes das tarefas atribuídas a ele.
/// </summary>
public class AcessoService(ApplicationDbContext db, UserManager<Usuario> userManager)
{
    public sealed class Escopo
    {
        public required Usuario Usuario { get; init; }
        public bool EhInterno { get; init; }
        public bool EhCliente { get; init; }
        public bool EhColaboradorExterno { get; init; }
        public int? ClienteProprioId { get; init; }
    }

    public async Task<Escopo?> ObterAsync(ClaimsPrincipal principal)
    {
        var usuario = await userManager.GetUserAsync(principal);
        if (usuario == null || !usuario.Ativo) return null;

        var ehAdmin = principal.IsInRole("Admin");
        var ehCliente = !ehAdmin && (usuario.Tipo == TipoUsuario.Cliente || principal.IsInRole("Cliente"));
        var ehColab = !ehAdmin && !ehCliente && (usuario.Tipo == TipoUsuario.ColaboradorExterno || principal.IsInRole("ColaboradorExterno"));
        var ehInterno = !ehCliente && !ehColab && (ehAdmin || principal.IsInRole("Equipe"));

        int? clienteId = null;
        if (ehCliente)
            clienteId = await db.Clientes.Where(c => c.UsuarioClienteId == usuario.Id && c.Ativo)
                .Select(c => (int?)c.Id).FirstOrDefaultAsync();

        return new Escopo
        {
            Usuario = usuario, EhInterno = ehInterno, EhCliente = ehCliente,
            EhColaboradorExterno = ehColab, ClienteProprioId = clienteId
        };
    }

    /// <summary>Clientes que o usuário pode ver (sempre dentro da agência atual).</summary>
    public IQueryable<Cliente> Clientes(Escopo? e)
    {
        if (e == null) return db.Clientes.Where(_ => false);
        if (e.EhInterno) return db.Clientes;
        if (e.EhCliente)
        {
            var id = e.ClienteProprioId;
            return db.Clientes.Where(c => id != null && c.Id == id);
        }
        if (e.EhColaboradorExterno)
        {
            var uid = e.Usuario.Id;
            return db.Clientes.Where(c => db.Tarefas.Any(t => t.ClienteId == c.Id && t.ResponsavelId == uid));
        }
        return db.Clientes.Where(_ => false);
    }

    public Task<bool> PodeVerClienteAsync(Escopo? e, int clienteId) =>
        Clientes(e).AnyAsync(c => c.Id == clienteId);
}
