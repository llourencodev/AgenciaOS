using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Email;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Contas;

public class CadastroAgenciaInput
{
    [Required(ErrorMessage = "Informe o nome da agência."), StringLength(150)] public string NomeAgencia { get; set; } = "";
    [StringLength(18)] public string? Cnpj { get; set; }
    [StringLength(30)] public string? Telefone { get; set; }
    [Required(ErrorMessage = "Informe seu nome."), StringLength(100)] public string NomeAdmin { get; set; } = "";
    [Required(ErrorMessage = "Informe seu e-mail."), EmailAddress(ErrorMessage = "E-mail inválido."), StringLength(256)] public string Email { get; set; } = "";
    [Required(ErrorMessage = "Crie uma senha."), DataType(DataType.Password)] public string Senha { get; set; } = "";
    [Compare(nameof(Senha), ErrorMessage = "As senhas não conferem."), DataType(DataType.Password)] public string ConfirmarSenha { get; set; } = "";
    public bool AceitaTermos { get; set; }
    /// <summary>Honeypot: campo invisível; robôs preenchem.</summary>
    public string? Website { get; set; }
}

/// <summary>
/// Cadastro público de agências, convites e links de conta. Opera em modo Sistema apenas nos trechos
/// que criam dados de uma agência nova (o visitante ainda não pertence a nenhuma).
/// </summary>
public class ContasService(
    ApplicationDbContext db,
    UserManager<Usuario> userManager,
    TenantContext tenant,
    IEnviadorEmail email,
    EmailOptions emailOpt,
    IHostEnvironment env,
    ILogger<ContasService> logger)
{
    public const int DiasTeste = 14;
    public static readonly TimeSpan ValidadeConvite = TimeSpan.FromDays(7);

    // ── Links ───────────────────────────────────────────────────────────
    /// <summary>
    /// Base dos links de e-mail. Em produção vem de APP_BASE_URL (evita envenenamento do link pelo cabeçalho Host).
    /// </summary>
    public string UrlBase(HttpRequest req)
    {
        if (!string.IsNullOrEmpty(emailOpt.UrlBase)) return emailOpt.UrlBase;
        if (Operacao.AmbienteExtensions.EhProducaoOuHomologacao(env)) throw new InvalidOperationException("Defina APP_BASE_URL para enviar links por e-mail em produção.");
        return $"{req.Scheme}://{req.Host}";
    }

    public string Link(HttpRequest req, string caminho, IDictionary<string, string?> query) =>
        QueryHelpers.AddQueryString(UrlBase(req) + caminho, query);

    // ── Cadastro público ────────────────────────────────────────────────
    public record ResultadoCadastro(bool Ok, IReadOnlyList<string> Erros);

    public async Task<ResultadoCadastro> CadastrarAgenciaAsync(CadastroAgenciaInput input, HttpRequest req, CancellationToken ct = default)
    {
        var emailNormalizado = input.Email.Trim();

        // Anti-enumeração: e-mail já usado → mesma resposta de sucesso, e o dono do e-mail é avisado
        if (await userManager.FindByEmailAsync(emailNormalizado) is { } existente)
        {
            await email.EnviarAsync(TemplatesEmail.CadastroComEmailExistente(emailNormalizado,
                Link(req, "/Account/Login", new Dictionary<string, string?>()),
                Link(req, "/Account/EsqueciSenha", new Dictionary<string, string?>())), ct);
            logger.LogInformation("Cadastro com e-mail já existente (usuário {Id})", existente.Id);
            return new(true, []);
        }

        // Política de senha do Identity antes de criar qualquer coisa
        var erros = new List<string>();
        foreach (var v in userManager.PasswordValidators)
        {
            var r = await v.ValidateAsync(userManager, new Usuario { UserName = emailNormalizado, Email = emailNormalizado }, input.Senha);
            erros.AddRange(r.Errors.Select(e => e.Description));
        }
        if (erros.Count > 0) return new(false, erros);

        tenant.UsarSistema();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var agencia = new Agencia
        {
            Nome = input.NomeAgencia.Trim(),
            Slug = await SlugDisponivelAsync(input.NomeAgencia, ct),
            Cnpj = string.IsNullOrWhiteSpace(input.Cnpj) ? null : input.Cnpj.Trim(),
            Telefone = string.IsNullOrWhiteSpace(input.Telefone) ? null : input.Telefone.Trim(),
            EmailPrincipal = emailNormalizado,
            Status = StatusAgencia.Ativa,
            SituacaoAssinatura = SituacaoAssinatura.Teste,
            TesteExpiraEm = DateTime.UtcNow.AddDays(await db.ConfiguracoesPlataforma.Where(c => c.Id == 1).Select(c => (int?)c.DiasTeste).FirstOrDefaultAsync(ct) ?? DiasTeste)
        };
        db.Agencias.Add(agencia);
        await db.SaveChangesAsync(ct);
        db.Configuracoes.Add(new Configuracao { AgenciaId = agencia.Id, NomeAgencia = agencia.Nome });
        await db.SaveChangesAsync(ct);

        // Sempre Admin da agência nova. Nada no formulário escolhe papel ou agência.
        var admin = new Usuario
        {
            UserName = emailNormalizado, Email = emailNormalizado, Nome = input.NomeAdmin.Trim(),
            Tipo = TipoUsuario.Admin, AgenciaId = agencia.Id, EmailConfirmed = false
        };
        var criado = await userManager.CreateAsync(admin, input.Senha);
        if (!criado.Succeeded)
        {
            await tx.RollbackAsync(ct);
            return new(false, criado.Errors.Select(e => e.Description).ToList());
        }
        await userManager.AddToRoleAsync(admin, "Admin");
        RegistrarAceites(admin.Id, agencia.Id, req);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await EnviarConfirmacaoAsync(admin, agencia.Nome, req, ct);
        logger.LogInformation("Agência {AgenciaId} cadastrada pelo formulário público", agencia.Id);
        return new(true, []);
    }

    public void RegistrarAceites(string usuarioId, int? agenciaId, HttpRequest req)
    {
        var ip = req.HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = req.Headers.UserAgent.ToString();
        ua = ua.Length > 400 ? ua[..400] : ua;
        db.ConsentimentosTermos.AddRange(
            new ConsentimentoTermos { UsuarioId = usuarioId, AgenciaId = agenciaId, Documento = DocumentosLegais.Termos, Versao = DocumentosLegais.VersaoTermos, Ip = ip, UserAgent = ua },
            new ConsentimentoTermos { UsuarioId = usuarioId, AgenciaId = agenciaId, Documento = DocumentosLegais.Privacidade, Versao = DocumentosLegais.VersaoPrivacidade, Ip = ip, UserAgent = ua });
    }

    public async Task EnviarConfirmacaoAsync(Usuario usuario, string nomeAgencia, HttpRequest req, CancellationToken ct = default)
    {
        var token = await userManager.GenerateEmailConfirmationTokenAsync(usuario);
        var link = Link(req, "/Account/ConfirmarEmail", new Dictionary<string, string?>
        {
            ["userId"] = usuario.Id,
            ["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))
        });
        await email.EnviarAsync(TemplatesEmail.ConfirmacaoCadastro(usuario.Email!, usuario.Nome, nomeAgencia, link), ct);
    }

    public async Task EnviarRecuperacaoAsync(Usuario usuario, HttpRequest req, CancellationToken ct = default)
    {
        var token = await userManager.GeneratePasswordResetTokenAsync(usuario);
        var link = Link(req, "/Account/RedefinirSenha", new Dictionary<string, string?>
        {
            ["email"] = usuario.Email,
            ["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))
        });
        await email.EnviarAsync(TemplatesEmail.RecuperacaoSenha(usuario.Email!, usuario.Nome, link), ct);
    }

    public static string? DecodificarToken(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }
    }

    private async Task<string> SlugDisponivelAsync(string nome, CancellationToken ct)
    {
        var baseSlug = Controllers.PlataformaController.GerarSlug(nome);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "agencia";
        if (baseSlug.Length > 50) baseSlug = baseSlug[..50].Trim('-');
        var slug = baseSlug;
        for (var i = 2; await db.Agencias.AnyAsync(a => a.Slug == slug, ct); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    // ── Convites ────────────────────────────────────────────────────────
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Cria (ou renova) o convite na agência atual e envia o e-mail. Chamado por um Admin da agência.</summary>
    public async Task<(bool Ok, string? Erro)> ConvidarAsync(string emailConvidado, string? nome, TipoUsuario tipo, string? abas,
        Usuario quemConvida, HttpRequest req, CancellationToken ct = default)
    {
        emailConvidado = emailConvidado.Trim();
        if (!new EmailAddressAttribute().IsValid(emailConvidado)) return (false, "E-mail inválido.");
        if (await userManager.FindByEmailAsync(emailConvidado) != null)
            return (false, "Este e-mail já tem acesso ao Midia+.");

        // Convites pendentes anteriores para o mesmo e-mail são revogados (só o último link vale)
        await db.ConvitesUsuario.Where(c => c.Email == emailConvidado && c.AceitoEm == null && c.RevogadoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevogadoEm, DateTime.UtcNow), ct);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var convite = new ConviteUsuario
        {
            Email = emailConvidado, Nome = nome?.Trim(), Tipo = tipo, AbasPermitidas = abas,
            TokenHash = HashToken(token), ExpiraEm = DateTime.UtcNow.Add(ValidadeConvite), CriadoPorId = quemConvida.Id
        };
        db.ConvitesUsuario.Add(convite);
        await db.SaveChangesAsync(ct);

        var nomeAgencia = await db.Configuracoes.OrderBy(c => c.Id).Select(c => c.NomeAgencia).FirstOrDefaultAsync(ct)
                          ?? await db.Agencias.Where(a => a.Id == convite.AgenciaId).Select(a => a.Nome).FirstAsync(ct);
        var link = Link(req, "/Account/Convite", new Dictionary<string, string?> { ["token"] = token });
        await email.EnviarAsync(TemplatesEmail.Convite(emailConvidado, nomeAgencia, quemConvida.NomeCompleto, link, convite.ExpiraEm), ct);
        return (true, null);
    }

    /// <summary>Convite válido pelo token (modo Sistema: o visitante ainda não tem agência).</summary>
    public async Task<(ConviteUsuario? Convite, string? NomeAgencia)> BuscarConviteAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return (null, null);
        tenant.UsarSistema();
        var hash = HashToken(token);
        var convite = await db.ConvitesUsuario.FirstOrDefaultAsync(c => c.TokenHash == hash, ct);
        if (convite is not { Pendente: true }) return (null, null);
        var agencia = await db.Agencias.FirstAsync(a => a.Id == convite.AgenciaId, ct);
        if (agencia.Status != StatusAgencia.Ativa) return (null, null);
        var nome = await db.Configuracoes.Where(c => c.AgenciaId == agencia.Id).OrderBy(c => c.Id).Select(c => c.NomeAgencia).FirstOrDefaultAsync(ct);
        return (convite, nome ?? agencia.NomeExibicao);
    }

    public async Task<IReadOnlyList<string>> AceitarConviteAsync(string token, string nome, string senha, bool aceitaTermos, HttpRequest req, CancellationToken ct = default)
    {
        var (convite, _) = await BuscarConviteAsync(token, ct);
        if (convite == null) return ["Convite inválido, expirado ou já utilizado."];
        if (!aceitaTermos) return ["É preciso aceitar os Termos de Uso e a Política de Privacidade."];
        if (string.IsNullOrWhiteSpace(nome)) return ["Informe seu nome."];

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var usuario = new Usuario
        {
            UserName = convite.Email, Email = convite.Email, Nome = nome.Trim(), Tipo = convite.Tipo,
            AgenciaId = convite.AgenciaId, AbasPermitidas = convite.AbasPermitidas,
            EmailConfirmed = true // o link chegou neste e-mail
        };
        var r = await userManager.CreateAsync(usuario, senha);
        if (!r.Succeeded)
        {
            await tx.RollbackAsync(ct);
            return r.Errors.Select(e => e.Description).ToList();
        }
        await userManager.AddToRoleAsync(usuario, convite.Tipo switch
        {
            TipoUsuario.Admin => "Admin",
            TipoUsuario.Cliente => "Cliente",
            TipoUsuario.ColaboradorExterno => "ColaboradorExterno",
            _ => "Equipe"
        });
        convite.AceitoEm = DateTime.UtcNow;
        RegistrarAceites(usuario.Id, convite.AgenciaId, req);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return [];
    }
}
