using AgenciaOS.Data;
using AgenciaOS.Models;

namespace AgenciaOS.Services;

public class NotificacaoService(ApplicationDbContext db)
{
    public async Task CriarAsync(string usuarioId, string titulo, string mensagem, TipoNotificacao tipo, string? url = null)
    {
        db.Notificacoes.Add(new Notificacao
        {
            UsuarioId = usuarioId,
            Titulo = titulo,
            Mensagem = mensagem,
            Tipo = tipo,
            Url = url
        });
        await db.SaveChangesAsync();
    }
}
