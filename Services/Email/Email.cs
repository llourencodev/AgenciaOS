using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Encodings.Web;

namespace AgenciaOS.Services.Email;

public record MensagemEmail(string Para, string Assunto, string Html, string Texto, string? Categoria = null);

/// <summary>Envio de e-mails transacionais. Os módulos só conhecem esta interface (fornecedor trocável).</summary>
public interface IEnviadorEmail
{
    Task EnviarAsync(MensagemEmail mensagem, CancellationToken ct = default);
}

public class EmailOptions
{
    /// <summary>"log" (desenvolvimento: só registra), "memoria" (testes) ou "resend".</summary>
    public string Provedor { get; set; } = "log";
    public string Remetente { get; set; } = "Midia+ <nao-responda@agenciaos.local>";
    public string? ResendApiKey { get; set; }
    /// <summary>Base dos links enviados por e-mail. Nunca vem do cabeçalho Host da requisição em produção.</summary>
    public string? UrlBase { get; set; }

    public static EmailOptions Carregar(IConfiguration cfg) => new()
    {
        Provedor = (cfg["EMAIL_PROVIDER"] ?? "log").Trim().ToLowerInvariant(),
        Remetente = cfg["EMAIL_FROM"] ?? "Midia+ <nao-responda@agenciaos.local>",
        ResendApiKey = cfg["RESEND_API_KEY"],
        UrlBase = cfg["APP_BASE_URL"]?.TrimEnd('/'),
    };
}

/// <summary>Desenvolvimento: registra no log que o e-mail seria enviado (com o link, para testar o fluxo localmente).</summary>
public class EnviadorEmailLog(ILogger<EnviadorEmailLog> logger) : IEnviadorEmail
{
    public Task EnviarAsync(MensagemEmail m, CancellationToken ct = default)
    {
        logger.LogWarning("E-mail NÃO enviado (EMAIL_PROVIDER=log) para {Para}: {Assunto}\n{Texto}", m.Para, m.Assunto, m.Texto);
        return Task.CompletedTask;
    }
}

/// <summary>Testes automatizados: guarda as mensagens para inspeção.</summary>
public class EnviadorEmailMemoria : IEnviadorEmail
{
    public ConcurrentQueue<MensagemEmail> Enviadas { get; } = new();
    public Task EnviarAsync(MensagemEmail m, CancellationToken ct = default)
    {
        Enviadas.Enqueue(m);
        return Task.CompletedTask;
    }
}

/// <summary>Resend (https://resend.com). Chave em RESEND_API_KEY; remetente de domínio verificado em EMAIL_FROM.</summary>
public class EnviadorEmailResend(IHttpClientFactory http, EmailOptions opt, ILogger<EnviadorEmailResend> logger) : IEnviadorEmail
{
    public const string NomeCliente = "resend";

    public async Task EnviarAsync(MensagemEmail m, CancellationToken ct = default)
    {
        var cliente = http.CreateClient(NomeCliente);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new { from = opt.Remetente, to = new[] { m.Para }, subject = m.Assunto, html = m.Html, text = m.Texto })
        };
        req.Headers.Authorization = new("Bearer", opt.ResendApiKey);
        using var resp = await cliente.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Não registra o corpo da mensagem (pode conter tokens)
            logger.LogError("Resend recusou o e-mail {Categoria} ({Status})", m.Categoria, (int)resp.StatusCode);
            throw new InvalidOperationException($"Falha ao enviar e-mail ({(int)resp.StatusCode}).");
        }
    }
}

/// <summary>Templates transacionais com a identidade do AgenciaOS (tabelas + CSS inline, responsivos).</summary>
public static class TemplatesEmail
{
    private static string H(string s) => HtmlEncoder.Default.Encode(s);

    public static MensagemEmail Montar(string para, string assunto, string titulo, string paragrafoHtmlSeguro,
        string? textoBotao = null, string? link = null, string? rodape = null, string? categoria = null)
    {
        var botao = link == null ? "" : $"""
            <tr><td style="padding:8px 32px 24px">
              <a href="{H(link)}" style="display:inline-block;background:#6366f1;color:#ffffff;text-decoration:none;font-weight:600;padding:12px 24px;border-radius:10px">{H(textoBotao ?? "Abrir")}</a>
            </td></tr>
            <tr><td style="padding:0 32px 24px;font-size:12px;color:#6b7280;word-break:break-all">Se o botão não funcionar, copie e cole no navegador:<br>{H(link)}</td></tr>
            """;

        var html = $"""
            <!DOCTYPE html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{H(assunto)}</title></head>
            <body style="margin:0;background:#f5f6fa;font-family:Inter,Segoe UI,Arial,sans-serif;color:#111827">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f5f6fa;padding:24px 12px"><tr><td align="center">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#ffffff;border-radius:14px;overflow:hidden">
                <tr><td style="background:#0f0d22;padding:20px 32px;color:#ffffff;font-weight:800;font-size:20px;letter-spacing:-.02em">Midia<span style="color:#8b6bff">+</span></td></tr>
                <tr><td style="padding:28px 32px 8px;font-size:20px;font-weight:700">{H(titulo)}</td></tr>
                <tr><td style="padding:8px 32px 16px;font-size:15px;line-height:1.6;color:#374151">{paragrafoHtmlSeguro}</td></tr>
                {botao}
                <tr><td style="padding:16px 32px 28px;font-size:12px;color:#9ca3af;border-top:1px solid #f0f0f5">{H(rodape ?? "Você recebeu esta mensagem porque há uma conta associada a este endereço no Midia+. Se não foi você, ignore este e-mail.")}</td></tr>
              </table>
            </td></tr></table></body></html>
            """;

        // Versão texto: sem tags e sem entidades HTML (acentos codificados viram caracteres de novo)
        var texto = $"{titulo}\n\n{System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(paragrafoHtmlSeguro, "<[^>]+>", ""))}\n\n{(link != null ? $"{textoBotao}: {link}\n\n" : "")}{rodape}";
        return new MensagemEmail(para, assunto, html, texto, categoria);
    }

    public static MensagemEmail ConfirmacaoCadastro(string para, string nome, string agencia, string link) =>
        Montar(para, "Confirme seu e-mail no Midia+", $"Olá, {nome}!",
            $"A conta da agência <strong>{H(agencia)}</strong> foi criada. Confirme seu e-mail para liberar o acesso. O link vale por 3 horas.",
            "Confirmar e-mail", link, categoria: "confirmacao");

    public static MensagemEmail CadastroComEmailExistente(string para, string linkLogin, string linkSenha) =>
        Montar(para, "Tentativa de cadastro no Midia+", "Você já tem uma conta",
            $"Alguém tentou criar uma nova agência usando este e-mail, que já está cadastrado. Se foi você, entre normalmente ou <a href=\"{H(linkSenha)}\">redefina sua senha</a>.",
            "Entrar", linkLogin, categoria: "cadastro-duplicado");

    public static MensagemEmail RecuperacaoSenha(string para, string nome, string link) =>
        Montar(para, "Redefinição de senha do Midia+", $"Olá, {nome}",
            "Recebemos um pedido para redefinir sua senha. O link vale por 3 horas. Ao redefinir, todas as sessões abertas serão encerradas.",
            "Redefinir senha", link, categoria: "recuperacao-senha");

    public static MensagemEmail Convite(string para, string agencia, string quemConvidou, string link, DateTime expiraEm) =>
        Montar(para, $"Convite para {agencia} no Midia+", "Você foi convidado",
            $"<strong>{H(quemConvidou)}</strong> convidou você para acessar a agência <strong>{H(agencia)}</strong> no Midia+. O convite vale até {expiraEm.ToLocalTime():dd/MM/yyyy HH:mm}.",
            "Aceitar convite", link, categoria: "convite");

    public static MensagemEmail ConfirmacaoNovoEmail(string para, string nome, string link) =>
        Montar(para, "Confirme seu novo e-mail no Midia+", $"Olá, {nome}",
            "Foi pedida a troca do e-mail de acesso da sua conta para este endereço. Confirme para concluir a troca. Se não foi você, ignore esta mensagem.",
            "Confirmar novo e-mail", link, categoria: "troca-email");

    public static MensagemEmail SenhaAlterada(string para, string nome) =>
        Montar(para, "Sua senha do Midia+ foi alterada", $"Olá, {nome}",
            "A senha da sua conta foi alterada e as outras sessões foram encerradas. Se não foi você, redefina a senha imediatamente e avise o administrador da sua agência.",
            categoria: "senha-alterada");
}

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration cfg)
    {
        var opt = EmailOptions.Carregar(cfg);
        services.AddSingleton(opt);
        services.AddHttpClient(EnviadorEmailResend.NomeCliente, c => c.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers();
        switch (opt.Provedor)
        {
            case "resend":
                if (string.IsNullOrWhiteSpace(opt.ResendApiKey))
                    throw new InvalidOperationException("EMAIL_PROVIDER=resend exige RESEND_API_KEY.");
                services.AddSingleton<IEnviadorEmail, EnviadorEmailResend>();
                break;
            case "memoria":
                services.AddSingleton<EnviadorEmailMemoria>();
                services.AddSingleton<IEnviadorEmail>(sp => sp.GetRequiredService<EnviadorEmailMemoria>());
                break;
            case "log":
                services.AddSingleton<IEnviadorEmail, EnviadorEmailLog>();
                break;
            default:
                throw new InvalidOperationException($"EMAIL_PROVIDER desconhecido: {opt.Provedor}");
        }
        return services;
    }
}
