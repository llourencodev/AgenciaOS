using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Tenancy;
using DnsClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AgenciaOS.Services.Dominios;

/// <summary>Consulta de registros TXT (abstração para testes).</summary>
public interface IConsultaDns
{
    Task<IReadOnlyList<string>> TxtAsync(string nome, CancellationToken ct = default);
}

public class ConsultaDnsReal : IConsultaDns
{
    private readonly LookupClient _cliente = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    public async Task<IReadOnlyList<string>> TxtAsync(string nome, CancellationToken ct = default)
    {
        var r = await _cliente.QueryAsync(nome, QueryType.TXT, cancellationToken: ct);
        return r.Answers.TxtRecords().SelectMany(t => t.Text).ToList();
    }
}

/// <summary>
/// Configuração de domínios da plataforma (variáveis de ambiente):
/// PLATAFORMA_HOSTS = hosts do próprio AgenciaOS (ex.: app.agenciaos.com.br); quando definido, hosts desconhecidos recebem 404.
/// PLATAFORMA_DOMINIO_BASE = domínio para subdomínios automáticos ({slug}.agenciaos.com.br), já verificados.
/// PLATAFORMA_CNAME_ALVO = destino do CNAME que a agência configura (o domínio do serviço no Railway).
/// </summary>
public class DominiosOptions
{
    public HashSet<string> HostsPlataforma { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DominioBase { get; init; }
    public string? CnameAlvo { get; init; }

    public static DominiosOptions Carregar(IConfiguration cfg) => new()
    {
        HostsPlataforma = new HashSet<string>((cfg["PLATAFORMA_HOSTS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase),
        DominioBase = cfg["PLATAFORMA_DOMINIO_BASE"]?.Trim().TrimEnd('.').ToLowerInvariant(),
        CnameAlvo = cfg["PLATAFORMA_CNAME_ALVO"]?.Trim(),
    };
}

public partial class DominiosService(ApplicationDbContext db, IConsultaDns dns, DominiosOptions opt, IMemoryCache cache, TenantContext tenant)
{
    private const string ChaveCache = "dominios-verificados";

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex HostValido();

    /// <summary>Normaliza o que a pessoa digitou ("https://Painel.Agência.com.br/login" → "painel.xn--agncia-1ua.com.br").</summary>
    public static string? Normalizar(string? entrada)
    {
        if (string.IsNullOrWhiteSpace(entrada)) return null;
        var s = entrada.Trim().ToLowerInvariant();
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        s = s.Split('/', '?', '#')[0];
        if (s.Contains('@')) return null; // usuário embutido: recusar
        s = s.Split(':')[0].TrimEnd('.');
        try { s = new IdnMapping().GetAscii(s); } catch (ArgumentException) { return null; }
        return HostValido().IsMatch(s) ? s : null;
    }

    public bool EhHostDaPlataforma(string host) =>
        opt.HostsPlataforma.Contains(host) || host is "localhost" or "127.0.0.1";

    private bool SubdominioAutomatico(string host, Agencia agencia) =>
        opt.DominioBase != null && host == $"{agencia.Slug}.{opt.DominioBase}";

    private bool SubdominioReservado(string host) =>
        opt.DominioBase != null && (host == opt.DominioBase || host.EndsWith("." + opt.DominioBase));

    /// <summary>Cadastra um domínio na agência atual. Retorna erro legível ou null.</summary>
    public async Task<(DominioAgencia? Dominio, string? Erro)> AdicionarAsync(string entrada, string? usuarioId, CancellationToken ct = default)
    {
        var host = Normalizar(entrada);
        if (host == null) return (null, "Domínio inválido. Use algo como painel.suaagencia.com.br.");
        if (EhHostDaPlataforma(host)) return (null, "Este é um endereço do próprio Midia+.");

        var agencia = await db.Agencias.SingleAsync(a => a.Id == tenant.AgenciaId, ct);
        var automatico = SubdominioAutomatico(host, agencia);
        if (!automatico && SubdominioReservado(host))
            return (null, $"Subdomínios de {opt.DominioBase} são reservados. O seu é {agencia.Slug}.{opt.DominioBase}.");

        // Índice único: um host pertence a uma única agência (sem revelar qual)
        if (await db.DominiosAgencia.IgnoreQueryFilters().AnyAsync(d => d.Host == host, ct))
            return (null, "Este domínio já está cadastrado.");

        var d = new DominioAgencia
        {
            Host = host,
            TokenVerificacao = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            CriadoPorId = usuarioId,
            Status = automatico ? StatusDominio.Verificado : StatusDominio.Pendente,
            VerificadoEm = automatico ? DateTime.UtcNow : null,
            Principal = !await db.DominiosAgencia.AnyAsync(ct)
        };
        db.DominiosAgencia.Add(d);
        await db.SaveChangesAsync(ct);
        cache.Remove(ChaveCache);
        return (d, null);
    }

    /// <summary>Verifica a posse pelo TXT _agenciaos.{host} = token.</summary>
    public async Task<bool> VerificarAsync(DominioAgencia d, CancellationToken ct = default)
    {
        d.UltimaTentativaEm = DateTime.UtcNow;
        try
        {
            var registros = await dns.TxtAsync(d.NomeRegistroTxt, ct);
            if (registros.Any(r => r.Trim() == d.TokenVerificacao))
            {
                d.Status = StatusDominio.Verificado;
                d.VerificadoEm = DateTime.UtcNow;
                d.UltimoErro = null;
            }
            else
            {
                d.Status = StatusDominio.Falhou;
                d.UltimoErro = registros.Count == 0
                    ? $"Registro TXT {d.NomeRegistroTxt} não encontrado (a propagação do DNS pode levar algumas horas)."
                    : "Registro TXT encontrado, mas com valor diferente do token.";
            }
        }
        catch (Exception e) when (e is DnsResponseException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            d.Status = StatusDominio.Falhou;
            d.UltimoErro = "Não foi possível consultar o DNS agora. Tente novamente em alguns minutos.";
        }
        await db.SaveChangesAsync(ct);
        cache.Remove(ChaveCache);
        return d.Status == StatusDominio.Verificado;
    }

    public async Task RemoverAsync(DominioAgencia d, CancellationToken ct = default)
    {
        db.DominiosAgencia.Remove(d);
        await db.SaveChangesAsync(ct);
        cache.Remove(ChaveCache);
    }

    /// <summary>Nome e logo exibidos na tela de login de um domínio de agência (dados públicos de identidade visual).</summary>
    public async Task<(string Nome, string? LogoUrl)?> MarcaAsync(int agenciaId, CancellationToken ct = default)
    {
        var cfg = await db.Configuracoes.IgnoreQueryFilters().AsNoTracking().Where(c => c.AgenciaId == agenciaId).OrderBy(c => c.Id)
            .Select(c => new { c.NomeAgencia, c.LogoUrl }).FirstOrDefaultAsync(ct);
        if (cfg != null) return (cfg.NomeAgencia, cfg.LogoUrl);
        // Agência que ainda não personalizou a identidade visual: usa o nome cadastrado
        var agencia = await db.Agencias.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agenciaId, ct);
        return agencia == null ? null : (agencia.NomeExibicao, null);
    }

    /// <summary>Agência dona de um host verificado (cache de 5 min). Consulta fora do filtro de agência, só leitura.</summary>
    public async Task<int?> AgenciaDoHostAsync(string host, CancellationToken ct = default)
    {
        var mapa = await cache.GetOrCreateAsync(ChaveCache, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await db.DominiosAgencia.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Status == StatusDominio.Verificado)
                .Join(db.Agencias, d => d.AgenciaId, a => a.Id, (d, a) => new { d.Host, d.AgenciaId, a.Status })
                .Where(x => x.Status != StatusAgencia.Cancelada)
                .ToDictionaryAsync(x => x.Host, x => x.AgenciaId, StringComparer.OrdinalIgnoreCase, ct);
        });
        return mapa!.TryGetValue(host, out var id) ? id : null;
    }
}

/// <summary>
/// Identifica a agência pelo domínio da requisição, só para identidade visual e para recusar uso cruzado.
/// Nunca define a agência dos dados: isso vem sempre do usuário autenticado (TenantMiddleware).
/// Com PLATAFORMA_HOSTS definido, hosts que não são da plataforma nem verificados recebem 404.
/// </summary>
public class DominioMiddleware(RequestDelegate next)
{
    public const string ItemAgencia = "agencia-do-dominio";

    public async Task InvokeAsync(HttpContext http, DominiosService dominios, DominiosOptions opt)
    {
        var host = http.Request.Host.Host.ToLowerInvariant();
        var caminho = http.Request.Path;
        // Health checks e webhooks podem chegar pelo host interno do Railway
        var livre = caminho.StartsWithSegments("/health") || caminho.StartsWithSegments("/webhooks") || caminho.StartsWithSegments("/integracoes/meta") || caminho.StartsWithSegments("/integracoes/tiktok");

        if (!dominios.EhHostDaPlataforma(host))
        {
            var agencia = await dominios.AgenciaDoHostAsync(host);
            if (agencia is int id) http.Items[ItemAgencia] = id;
            else if (opt.HostsPlataforma.Count > 0 && !livre)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                await http.Response.WriteAsync("Domínio não configurado no Midia+.");
                return;
            }
        }
        await next(http);
    }
}
