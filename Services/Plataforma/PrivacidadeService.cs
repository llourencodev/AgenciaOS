using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Plataforma;

/// <summary>
/// Direitos dos titulares (LGPD art. 18): solicitações, exportação (portabilidade/acesso) e anonimização.
/// Exclusões respeitam retenções legais: registros financeiros e de aceite permanecem, sem dados identificáveis quando possível.
/// </summary>
public class PrivacidadeService(
    ApplicationDbContext db,
    UserManager<Usuario> userManager,
    TenantContext tenant,
    IEnviadorEmail email,
    ILogger<PrivacidadeService> logger)
{
    public const int PrazoDias = 15;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ── Solicitações ─────────────────────────────────────────────────────
    public async Task<SolicitacaoPrivacidade> CriarSolicitacaoAsync(string nome, string emailTitular, TipoSolicitacaoPrivacidade tipo,
        string descricao, Usuario? usuario, string? ip, CancellationToken ct = default)
    {
        tenant.UsarSistema(); // pedido público ou de um usuário: gravação fora do filtro de agência
        var s = new SolicitacaoPrivacidade
        {
            Protocolo = $"LGPD-{DateTime.UtcNow:yyyyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}",
            NomeTitular = nome.Trim(), EmailTitular = emailTitular.Trim(), Tipo = tipo,
            Descricao = descricao.Length > 4000 ? descricao[..4000] : descricao,
            UsuarioId = usuario?.Id, AgenciaId = usuario?.AgenciaId,
            PrazoEm = DateTime.UtcNow.AddDays(PrazoDias), Ip = ip
        };
        db.SolicitacoesPrivacidade.Add(s);
        await db.SaveChangesAsync(ct);

        try
        {
            await email.EnviarAsync(TemplatesEmail.Montar(s.EmailTitular, $"Recebemos sua solicitação ({s.Protocolo})", "Solicitação de privacidade recebida",
                $"Registramos seu pedido de <strong>{HtmlEncoder.Default.Encode(tipo.ToString())}</strong> com o protocolo <strong>{s.Protocolo}</strong>. " +
                $"Responderemos até {s.PrazoEm.ToLocalTime():dd/MM/yyyy}. Podemos pedir informações para confirmar sua identidade.",
                categoria: "privacidade"), ct);
        }
        catch (Exception e) { logger.LogError(e, "Falha ao confirmar solicitação {Protocolo} por e-mail", s.Protocolo); }
        return s;
    }

    public async Task ResponderAsync(SolicitacaoPrivacidade s, StatusSolicitacaoPrivacidade status, string? resposta, string atendenteId, CancellationToken ct = default)
    {
        s.Status = status;
        s.Resposta = string.IsNullOrWhiteSpace(resposta) ? s.Resposta : resposta.Trim();
        s.AtendidaPorId = atendenteId;
        if (status is StatusSolicitacaoPrivacidade.Concluida or StatusSolicitacaoPrivacidade.Recusada) s.ConcluidaEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (status is StatusSolicitacaoPrivacidade.Concluida or StatusSolicitacaoPrivacidade.Recusada && !string.IsNullOrWhiteSpace(s.Resposta))
        {
            try
            {
                await email.EnviarAsync(TemplatesEmail.Montar(s.EmailTitular, $"Resposta à solicitação {s.Protocolo}",
                    status == StatusSolicitacaoPrivacidade.Concluida ? "Solicitação atendida" : "Solicitação não atendida",
                    HtmlEncoder.Default.Encode(s.Resposta!), categoria: "privacidade"), ct);
            }
            catch (Exception e) { logger.LogError(e, "Falha ao enviar resposta da solicitação {Protocolo}", s.Protocolo); }
        }
    }

    // ── Exportação ───────────────────────────────────────────────────────
    /// <summary>Dados pessoais do próprio usuário (acesso/portabilidade). Nunca inclui hash de senha ou tokens.</summary>
    public async Task<byte[]> ExportarUsuarioAsync(string usuarioId, CancellationToken ct = default)
    {
        var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == usuarioId, ct);
        var dados = new
        {
            geradoEm = DateTime.UtcNow,
            titular = new { u.Id, u.Nome, u.Sobrenome, u.Email, u.PhoneNumber, u.Tipo, u.Ativo, u.CriadoEm, u.UltimoAcesso, u.EmailConfirmed, u.TwoFactorEnabled },
            agencia = u.AgenciaId == null ? null : await db.Agencias.AsNoTracking().Where(a => a.Id == u.AgenciaId).Select(a => new { a.Id, a.Nome }).FirstOrDefaultAsync(ct),
            perfis = await userManager.GetRolesAsync(u),
            aceites = await db.ConsentimentosTermos.AsNoTracking().Where(c => c.UsuarioId == usuarioId)
                .OrderBy(c => c.AceitoEm).Select(c => new { c.Documento, c.Versao, c.AceitoEm, c.Ip }).ToListAsync(ct),
            notificacoes = await db.Notificacoes.AsNoTracking().Where(n => n.UsuarioId == usuarioId)
                .OrderByDescending(n => n.CriadoEm).Select(n => new { n.Titulo, n.Mensagem, n.CriadoEm, n.Lida }).ToListAsync(ct),
            solicitacoesPrivacidade = await db.SolicitacoesPrivacidade.AsNoTracking().Where(s => s.UsuarioId == usuarioId)
                .Select(s => new { s.Protocolo, s.Tipo, s.Status, s.CriadaEm, s.ConcluidaEm }).ToListAsync(ct),
            clienteVinculado = await db.Clientes.IgnoreQueryFilters().AsNoTracking().Where(c => c.UsuarioClienteId == usuarioId)
                .Select(c => new { c.Id, c.NomeEmpresa, c.Responsavel, c.Email, c.Telefone }).FirstOrDefaultAsync(ct)
        };
        return JsonSerializer.SerializeToUtf8Bytes(dados, Json);
    }

    /// <summary>
    /// Exportação completa da agência atual (portabilidade/encerramento), em ZIP com um JSON por módulo.
    /// Usa os filtros de agência: só sai o que é da agência do usuário. Arquivos vão como lista (baixáveis pelo sistema).
    /// </summary>
    public async Task<byte[]> ExportarAgenciaAsync(CancellationToken ct = default)
    {
        var agenciaId = tenant.AgenciaId ?? throw new AcessoEntreAgenciasException("Exportação sem agência.");
        var partes = new Dictionary<string, object>
        {
            ["agencia.json"] = await db.Agencias.AsNoTracking().Where(a => a.Id == agenciaId)
                .Select(a => new { a.Id, a.Nome, a.NomeFantasia, a.Cnpj, a.EmailPrincipal, a.Telefone, a.CriadoEm }).SingleAsync(ct),
            ["configuracao.json"] = await db.Configuracoes.AsNoTracking().ToListAsync(ct),
            ["usuarios.json"] = await db.Users.AsNoTracking().Where(u => u.AgenciaId == agenciaId)
                .Select(u => new { u.Id, u.Nome, u.Sobrenome, u.Email, u.Tipo, u.Ativo, u.AbasPermitidas, u.CriadoEm, u.UltimoAcesso }).ToListAsync(ct),
            ["clientes.json"] = await db.Clientes.AsNoTracking().ToListAsync(ct),
            ["conteudos.json"] = await db.Conteudos.AsNoTracking().Include(c => c.Comentarios).Include(c => c.Historicos)
                .Select(c => new
                {
                    c.Id, c.ClienteId, c.Titulo, c.Descricao, c.Legenda, c.Tipo, c.Status, c.DataPrevista, c.DataPublicacao, c.Hashtags, c.CriadoEm,
                    Comentarios = c.Comentarios.Select(x => new { x.Texto, x.UsuarioId, x.CriadoEm }),
                    Historico = c.Historicos.Select(h => new { h.Descricao, h.StatusAnterior, h.StatusNovo, h.UsuarioId, h.CriadoEm })
                }).ToListAsync(ct),
            ["tarefas.json"] = await db.Tarefas.AsNoTracking().ToListAsync(ct),
            ["financeiro.json"] = await db.Financeiros.AsNoTracking().ToListAsync(ct),
            ["contratos.json"] = await db.Contratos.AsNoTracking().ToListAsync(ct),
            ["datas-comemorativas.json"] = await db.DatasComemorativas.AsNoTracking().ToListAsync(ct),
            ["arquivos.json"] = await db.Arquivos.AsNoTracking()
                .Select(a => new { a.Id, a.ClienteId, a.NomeOriginal, a.TipoMime, a.TamanhoBytes, a.Categoria, a.MesReferencia, a.HashSha256, a.CriadoEm,
                    Download = $"/Arquivos/Baixar/{a.Id}" }).ToListAsync(ct),
            ["marketing-contas.json"] = await db.ContasMarketing.AsNoTracking()
                .Select(c => new { c.Id, c.ClienteId, c.Plataforma, c.Nome, c.NomeUsuario, c.Status, c.UltimaSincronizacao }).ToListAsync(ct),
            ["marketing-metricas.json"] = await db.MetricasDiarias.AsNoTracking()
                .Select(m => new { m.ClienteId, m.Plataforma, m.Data, m.Metrica, m.Valor }).ToListAsync(ct),
            ["assinatura-pagamentos.json"] = await db.Pagamentos.AsNoTracking()
                .Select(p => new { p.Valor, p.Status, p.FormaPagamento, p.Vencimento, p.PagoEm }).ToListAsync(ct),
        };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (nome, valor) in partes)
            {
                var entrada = zip.CreateEntry(nome, CompressionLevel.Optimal);
                await using var s = entrada.Open();
                await JsonSerializer.SerializeAsync(s, valor, Json, ct);
            }
            var leia = zip.CreateEntry("LEIA-ME.txt");
            await using (var w = new StreamWriter(leia.Open()))
                await w.WriteAsync($"Exportação da agência {agenciaId} gerada em {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC.\n" +
                                   "Tokens de integração e senhas não são exportados. Os arquivos podem ser baixados pelos links em arquivos.json enquanto a conta existir.\n");
        }
        return ms.ToArray();
    }

    // ── Anonimização ─────────────────────────────────────────────────────
    /// <summary>
    /// Remove os dados identificáveis do usuário e encerra o acesso. O registro continua existindo (Id) para manter
    /// a integridade de tarefas, comentários e lançamentos; aceites de termos são mantidos como prova legal.
    /// </summary>
    public async Task AnonimizarUsuarioAsync(string usuarioId, CancellationToken ct = default)
    {
        var u = await userManager.FindByIdAsync(usuarioId) ?? throw new InvalidOperationException("Usuário não encontrado.");
        if (await userManager.IsInRoleAsync(u, "SuperAdmin")) throw new InvalidOperationException("Superadmin não pode ser anonimizado por aqui.");

        var anon = $"anonimizado-{Guid.NewGuid():N}@anonimizado.invalid";
        u.Nome = "Titular removido";
        u.Sobrenome = null;
        u.Foto = null;
        u.PhoneNumber = null;
        u.Email = anon;
        u.UserName = anon;
        u.NormalizedEmail = anon.ToUpperInvariant();
        u.NormalizedUserName = anon.ToUpperInvariant();
        u.EmailConfirmed = false;
        u.Ativo = false;
        u.PasswordHash = null;
        u.LockoutEnabled = true;
        u.LockoutEnd = DateTimeOffset.MaxValue;
        var r = await userManager.UpdateAsync(u);
        if (!r.Succeeded) throw new InvalidOperationException(string.Join("; ", r.Errors.Select(e => e.Description)));
        await userManager.UpdateSecurityStampAsync(u); // derruba sessões

        await db.UserLogins.Where(l => l.UserId == u.Id).ExecuteDeleteAsync(ct);
        await db.UserTokens.Where(t => t.UserId == u.Id).ExecuteDeleteAsync(ct);
        await db.UserClaims.Where(c => c.UserId == u.Id).ExecuteDeleteAsync(ct);
        await db.Notificacoes.Where(n => n.UsuarioId == u.Id).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Exclusão dos dados pessoais de um cliente da agência (a agência é a controladora). Lançamentos financeiros,
    /// contratos e tarefas permanecem (obrigações legais e histórico), vinculados a um cadastro sem dados pessoais.
    /// Conexões de marketing (tokens e métricas coletadas) são apagadas.
    /// </summary>
    public async Task AnonimizarClienteAsync(int clienteId, CancellationToken ct = default)
    {
        var c = await db.Clientes.FirstOrDefaultAsync(x => x.Id == clienteId, ct) ?? throw new InvalidOperationException("Cliente não encontrado.");
        c.NomeEmpresa = $"Cliente removido #{c.Id}";
        c.Responsavel = "—";
        c.Email = "";
        c.Telefone = null;
        c.Instagram = null;
        c.Observacoes = null;
        c.Estrategia = null;
        c.Objetivos = null;
        c.Logo = null;
        c.Ativo = false;
        var usuarioCliente = c.UsuarioClienteId;
        c.UsuarioClienteId = null;
        await db.SaveChangesAsync(ct);

        await db.ConexoesMarketing.Where(x => x.ClienteId == c.Id).ExecuteDeleteAsync(ct);
        if (usuarioCliente != null) await AnonimizarUsuarioAsync(usuarioCliente, ct);
    }
}
