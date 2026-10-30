using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace AgenciaOS.Services.Armazenamento;

/// <summary>
/// Armazenamento privado de arquivos. Os módulos só conhecem esta interface,
/// então trocar R2 por S3 (ou outro compatível) é questão de configuração.
/// </summary>
public interface IArmazenamentoArquivos
{
    /// <summary>"local" ou "s3" (gravado em Arquivo.Provedor).</summary>
    string Nome { get; }
    Task SalvarAsync(string chave, Stream conteudo, string contentType, CancellationToken ct = default);
    /// <summary>null se o objeto não existir.</summary>
    Task<Stream?> AbrirAsync(string chave, CancellationToken ct = default);
    Task ExcluirAsync(string chave, CancellationToken ct = default);
    Task<bool> ExisteAsync(string chave, CancellationToken ct = default);
    /// <summary>URL assinada de curta duração; null quando o provedor não gera URLs (a aplicação faz o streaming).</summary>
    string? GerarUrlTemporaria(string chave, TimeSpan validade, string nomeDownload, string contentType, bool inline);
}

/// <summary>Configuração por variáveis de ambiente (nunca no repositório).</summary>
public class ArmazenamentoOptions
{
    public string Provedor { get; set; } = "local";
    public string CaminhoLocal { get; set; } = "";
    public string? S3Endpoint { get; set; }
    public string S3Regiao { get; set; } = "auto";
    public string? S3Bucket { get; set; }
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    public bool S3ForcePathStyle { get; set; } = true;
    public TimeSpan ExpiracaoUrl { get; set; } = TimeSpan.FromMinutes(5);
    public long TamanhoMaximoBytes { get; set; } = 50L * 1024 * 1024;

    public static ArmazenamentoOptions Carregar(IConfiguration cfg, IHostEnvironment env) => new()
    {
        Provedor = (cfg["STORAGE_PROVIDER"] ?? "local").Trim().ToLowerInvariant(),
        // Fora de wwwroot: nada aqui é servido como arquivo estático
        CaminhoLocal = cfg["STORAGE_LOCAL_PATH"] ?? Path.Combine(env.ContentRootPath, "App_Data", "storage"),
        S3Endpoint = cfg["S3_ENDPOINT"],
        S3Regiao = cfg["S3_REGION"] ?? "auto",
        S3Bucket = cfg["S3_BUCKET"],
        S3AccessKey = cfg["S3_ACCESS_KEY_ID"],
        S3SecretKey = cfg["S3_SECRET_ACCESS_KEY"],
        S3ForcePathStyle = !string.Equals(cfg["S3_FORCE_PATH_STYLE"], "false", StringComparison.OrdinalIgnoreCase),
        ExpiracaoUrl = TimeSpan.FromSeconds(int.TryParse(cfg["STORAGE_URL_TTL_SECONDS"], out var s) && s is >= 30 and <= 3600 ? s : 300),
        TamanhoMaximoBytes = (long.TryParse(cfg["STORAGE_MAX_MB"], out var mb) && mb is > 0 and <= 500 ? mb : 50) * 1024 * 1024,
    };
}

public static class ChavesArmazenamento
{
    private static readonly Regex Valida = new("^agencies/[0-9]+/[a-z0-9/_.-]+$", RegexOptions.Compiled);

    /// <summary>agencies/{agenciaId}/clients/{clienteId}/{yyyy}/{MM}/{guid}{ext}</summary>
    public static string ArquivoCliente(int agenciaId, int clienteId, string extensao, DateTime? data = null)
    {
        var d = data ?? DateTime.UtcNow;
        return $"agencies/{agenciaId}/clients/{clienteId}/{d:yyyy}/{d:MM}/{Guid.NewGuid():N}{extensao.ToLowerInvariant()}";
    }

    /// <summary>agencies/{agenciaId}/brand/{nome}</summary>
    public static string Marca(int agenciaId, string nome) => $"agencies/{agenciaId}/brand/{nome}";

    /// <summary>Defesa contra path traversal e chaves fora do espaço de uma agência.</summary>
    public static void Validar(string chave)
    {
        if (string.IsNullOrEmpty(chave) || chave.Length > 500 || chave.Contains("..") || chave.Contains("//") || !Valida.IsMatch(chave))
            throw new ArgumentException($"Chave de armazenamento inválida: {chave}");
    }
}

/// <summary>Disco local fora de wwwroot (desenvolvimento, ou Railway com volume montado).</summary>
public class ArmazenamentoLocal(ArmazenamentoOptions opt) : IArmazenamentoArquivos
{
    private readonly string _raiz = Path.GetFullPath(opt.CaminhoLocal);

    public string Nome => "local";

    internal string Caminho(string chave)
    {
        ChavesArmazenamento.Validar(chave);
        var caminho = Path.GetFullPath(Path.Combine(_raiz, chave.Replace('/', Path.DirectorySeparatorChar)));
        if (!caminho.StartsWith(_raiz + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Chave fora da raiz do armazenamento.");
        return caminho;
    }

    public async Task SalvarAsync(string chave, Stream conteudo, string contentType, CancellationToken ct = default)
    {
        var caminho = Caminho(chave);
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        // Grava direto no destino (chaves são únicas). Sem rename: no Windows o antivírus pode
        // segurar o arquivo recém-criado e o Move falha com "Access denied".
        try
        {
            await using var fs = new FileStream(caminho, FileMode.Create, FileAccess.Write, FileShare.None);
            await conteudo.CopyToAsync(fs, ct);
        }
        catch
        {
            try { File.Delete(caminho); } catch { /* melhor esforço: não deixa arquivo parcial */ }
            throw;
        }
    }

    public Task<Stream?> AbrirAsync(string chave, CancellationToken ct = default)
    {
        var caminho = Caminho(chave);
        return Task.FromResult<Stream?>(File.Exists(caminho)
            ? new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    public Task ExcluirAsync(string chave, CancellationToken ct = default)
    {
        var caminho = Caminho(chave);
        if (File.Exists(caminho)) File.Delete(caminho);
        return Task.CompletedTask;
    }

    public Task<bool> ExisteAsync(string chave, CancellationToken ct = default) => Task.FromResult(File.Exists(Caminho(chave)));

    public string? GerarUrlTemporaria(string chave, TimeSpan validade, string nomeDownload, string contentType, bool inline) => null;
}

/// <summary>Cloudflare R2, Amazon S3 ou qualquer serviço compatível (bucket privado).</summary>
public class ArmazenamentoS3 : IArmazenamentoArquivos, IDisposable
{
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;
    private readonly bool _http;

    public ArmazenamentoS3(ArmazenamentoOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.S3Bucket) || string.IsNullOrWhiteSpace(opt.S3AccessKey) || string.IsNullOrWhiteSpace(opt.S3SecretKey))
            throw new InvalidOperationException("STORAGE_PROVIDER=s3 exige S3_BUCKET, S3_ACCESS_KEY_ID e S3_SECRET_ACCESS_KEY.");

        var cfg = new AmazonS3Config
        {
            ForcePathStyle = opt.S3ForcePathStyle,
            AuthenticationRegion = opt.S3Regiao,
            // R2 e outros compatíveis não aceitam os checksums automáticos do SDK v4
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(opt.S3Endpoint)) cfg.ServiceURL = opt.S3Endpoint;
        else cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(opt.S3Regiao);

        _s3 = new AmazonS3Client(new BasicAWSCredentials(opt.S3AccessKey, opt.S3SecretKey), cfg);
        _bucket = opt.S3Bucket;
        _http = opt.S3Endpoint?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true;
    }

    public string Nome => "s3";

    public async Task SalvarAsync(string chave, Stream conteudo, string contentType, CancellationToken ct = default)
    {
        ChavesArmazenamento.Validar(chave);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = chave, InputStream = conteudo, ContentType = contentType,
            AutoCloseStream = false, UseChunkEncoding = false
        }, ct);
    }

    public async Task<Stream?> AbrirAsync(string chave, CancellationToken ct = default)
    {
        ChavesArmazenamento.Validar(chave);
        try
        {
            var r = await _s3.GetObjectAsync(_bucket, chave, ct);
            return r.ResponseStream;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task ExcluirAsync(string chave, CancellationToken ct = default)
    {
        ChavesArmazenamento.Validar(chave);
        await _s3.DeleteObjectAsync(_bucket, chave, ct);
    }

    public async Task<bool> ExisteAsync(string chave, CancellationToken ct = default)
    {
        ChavesArmazenamento.Validar(chave);
        try { await _s3.GetObjectMetadataAsync(_bucket, chave, ct); return true; }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return false; }
    }

    public string? GerarUrlTemporaria(string chave, TimeSpan validade, string nomeDownload, string contentType, bool inline)
    {
        ChavesArmazenamento.Validar(chave);
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _bucket, Key = chave, Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validade),
            Protocol = _http ? Protocol.HTTP : Protocol.HTTPS,
        };
        req.ResponseHeaderOverrides.ContentType = contentType;
        req.ResponseHeaderOverrides.ContentDisposition = ValidadorArquivos.ContentDisposition(nomeDownload, inline);
        return _s3.GetPreSignedURL(req);
    }

    public void Dispose() => _s3.Dispose();
}

public static class ArmazenamentoServiceCollectionExtensions
{
    public static IServiceCollection AddArmazenamento(this IServiceCollection services, IConfiguration cfg, IHostEnvironment env)
    {
        var opt = ArmazenamentoOptions.Carregar(cfg, env);
        services.AddSingleton(opt);
        services.AddSingleton<IArmazenamentoArquivos>(sp => opt.Provedor switch
        {
            "s3" or "r2" => new ArmazenamentoS3(opt),
            "local" => new ArmazenamentoLocal(opt),
            _ => throw new InvalidOperationException($"STORAGE_PROVIDER desconhecido: {opt.Provedor}")
        });
        services.AddSingleton<ValidadorArquivos>();
        services.AddScoped<ServicoArquivos>();
        services.AddScoped<MigracaoArquivosService>();
        return services;
    }
}
