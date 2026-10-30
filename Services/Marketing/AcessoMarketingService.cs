using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Ponto único de isolamento do módulo de marketing: decide quais clientes o usuário pode ver.
/// Toda consulta do módulo deve filtrar pelos IDs retornados aqui.
/// </summary>
public class AcessoMarketingService(ApplicationDbContext db)
{
    public const string Aba = "Marketing";

    public class Contexto
    {
        public required Usuario Usuario { get; init; }
        public bool EhAgencia { get; init; }
        public int? ClienteProprioId { get; init; }
        public bool PodeAcessarModulo { get; init; }

        /// <summary>IDs permitidos; null = todos (somente agência).</summary>
        public IReadOnlyCollection<int>? ClientesPermitidos =>
            EhAgencia ? null : ClienteProprioId is int id ? [id] : [];

        public bool PodeAcessarCliente(int clienteId) =>
            PodeAcessarModulo && (EhAgencia || ClienteProprioId == clienteId);
    }

    public async Task<Contexto> ObterAsync(Usuario usuario, bool ehAdmin, bool ehEquipe)
    {
        var abas = usuario.AbasPermitidas?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var temAba = abas.Length == 0 || abas.Contains(Aba);

        if (!usuario.Ativo)
            return new Contexto { Usuario = usuario };

        if (ehAdmin || (ehEquipe && usuario.Tipo != TipoUsuario.Cliente && temAba))
            return new Contexto { Usuario = usuario, EhAgencia = true, PodeAcessarModulo = true };

        if (usuario.Tipo == TipoUsuario.Cliente)
        {
            var clienteId = await db.Clientes
                .Where(c => c.UsuarioClienteId == usuario.Id && c.Ativo)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync();
            return new Contexto
            {
                Usuario = usuario,
                ClienteProprioId = clienteId,
                PodeAcessarModulo = clienteId != null && temAba
            };
        }

        // ColaboradorExterno (ou Equipe sem a aba Marketing) não acessa o módulo
        return new Contexto { Usuario = usuario };
    }

    /// <summary>Clientes visíveis para o usuário (já filtrados).</summary>
    public IQueryable<Cliente> Clientes(Contexto ctx)
    {
        if (!ctx.PodeAcessarModulo) return db.Clientes.Where(_ => false);
        if (ctx.EhAgencia) return db.Clientes.Where(c => c.Ativo);
        var id = ctx.ClienteProprioId;
        return db.Clientes.Where(c => c.Id == id);
    }

    /// <summary>Contas visíveis para o usuário (já filtradas).</summary>
    public IQueryable<ContaMarketing> Contas(Contexto ctx)
    {
        if (!ctx.PodeAcessarModulo) return db.ContasMarketing.Where(_ => false);
        if (ctx.EhAgencia) return db.ContasMarketing;
        var id = ctx.ClienteProprioId;
        return db.ContasMarketing.Where(c => c.ClienteId == id);
    }
}
