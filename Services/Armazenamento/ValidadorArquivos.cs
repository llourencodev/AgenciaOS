using System.Text;

namespace AgenciaOS.Services.Armazenamento;

/// <summary>
/// Validação de uploads: extensão em lista branca, assinatura binária (magic bytes) compatível com a extensão,
/// bloqueio de executáveis, limite de tamanho e MIME definido pelo servidor (o Content-Type do navegador é ignorado).
/// </summary>
public class ValidadorArquivos(ArmazenamentoOptions opt)
{
    private record Tipo(string Mime, Func<byte[], bool> Assinatura, bool PodeExibirNoNavegador = false);

    private static bool Comeca(byte[] b, params byte[] sig) => b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);
    private static bool Ascii(byte[] b, int pos, string s) => b.Length >= pos + s.Length && Encoding.ASCII.GetString(b, pos, s.Length) == s;
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static bool Texto(byte[] b) => !b.Take(8192).Contains((byte)0);
    private static bool Svg(byte[] b)
    {
        if (!Texto(b)) return false;
        var s = Encoding.UTF8.GetString(b).ToLowerInvariant();
        return s.Contains("<svg");
    }

    private static readonly Dictionary<string, Tipo> Tipos = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"]  = new("image/jpeg", b => Comeca(b, 0xFF, 0xD8, 0xFF), true),
        [".jpeg"] = new("image/jpeg", b => Comeca(b, 0xFF, 0xD8, 0xFF), true),
        [".png"]  = new("image/png", b => Comeca(b, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), true),
        [".gif"]  = new("image/gif", b => Ascii(b, 0, "GIF87a") || Ascii(b, 0, "GIF89a"), true),
        [".webp"] = new("image/webp", b => Ascii(b, 0, "RIFF") && Ascii(b, 8, "WEBP"), true),
        // SVG pode conter script: aceito, mas sempre entregue como download, nunca exibido inline
        [".svg"]  = new("image/svg+xml", Svg),
        [".mp4"]  = new("video/mp4", b => Ascii(b, 4, "ftyp"), true),
        [".mov"]  = new("video/quicktime", b => Ascii(b, 4, "ftyp") || Ascii(b, 4, "moov") || Ascii(b, 4, "wide") || Ascii(b, 4, "mdat"), true),
        [".avi"]  = new("video/x-msvideo", b => Ascii(b, 0, "RIFF") && Ascii(b, 8, "AVI ")),
        [".webm"] = new("video/webm", b => Comeca(b, 0x1A, 0x45, 0xDF, 0xA3), true),
        [".pdf"]  = new("application/pdf", b => Ascii(b, 0, "%PDF-"), true),
        [".doc"]  = new("application/msword", b => Comeca(b, Ole)),
        [".xls"]  = new("application/vnd.ms-excel", b => Comeca(b, Ole)),
        [".ppt"]  = new("application/vnd.ms-powerpoint", b => Comeca(b, Ole)),
        [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", b => Comeca(b, Zip)),
        [".xlsx"] = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", b => Comeca(b, Zip)),
        [".pptx"] = new("application/vnd.openxmlformats-officedocument.presentationml.presentation", b => Comeca(b, Zip)),
        [".zip"]  = new("application/zip", b => Comeca(b, Zip) || Comeca(b, 0x50, 0x4B, 0x05, 0x06)),
        [".rar"]  = new("application/vnd.rar", b => Ascii(b, 0, "Rar!\u001A\u0007")),
        [".7z"]   = new("application/x-7z-compressed", b => Comeca(b, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)),
        [".txt"]  = new("text/plain", Texto),
        [".csv"]  = new("text/csv", Texto),
    };

    public static readonly string[] ExtensoesImagemMarca = [".png", ".jpg", ".jpeg", ".webp"];

    public long TamanhoMaximo => opt.TamanhoMaximoBytes;

    public record Resultado(bool Valido, string? Erro, string Extensao, string Mime, string NomeSeguro);

    /// <param name="cabecalho">Primeiros bytes do arquivo (8 KB bastam).</param>
    public Resultado Validar(string nomeOriginal, long tamanho, byte[] cabecalho, IReadOnlyCollection<string>? extensoesPermitidas = null, long? tamanhoMaximo = null)
    {
        var nome = NomeSeguro(nomeOriginal);
        var ext = Path.GetExtension(nome).ToLowerInvariant();
        Resultado Erro(string msg) => new(false, msg, ext, "application/octet-stream", nome);

        if (tamanho <= 0) return Erro("Arquivo vazio.");
        var max = tamanhoMaximo ?? opt.TamanhoMaximoBytes;
        if (tamanho > max) return Erro($"Arquivo muito grande. Limite: {max / 1024 / 1024} MB.");
        if (!Tipos.TryGetValue(ext, out var tipo) || (extensoesPermitidas != null && !extensoesPermitidas.Contains(ext)))
            return Erro($"Tipo de arquivo não permitido: {(string.IsNullOrEmpty(ext) ? "sem extensão" : ext)}");
        if (Executavel(cabecalho)) return Erro("Arquivo executável não é permitido.");
        if (!tipo.Assinatura(cabecalho)) return Erro($"O conteúdo do arquivo não corresponde à extensão {ext}.");

        return new(true, null, ext, tipo.Mime, nome);
    }

    public static bool PodeExibirNoNavegador(string mime) =>
        Tipos.Values.Any(t => t.Mime == mime && t.PodeExibirNoNavegador);

    private static bool Executavel(byte[] b) =>
        Ascii(b, 0, "MZ") || Comeca(b, 0x7F, 0x45, 0x4C, 0x46) || Ascii(b, 0, "#!")
        || Comeca(b, 0xCF, 0xFA, 0xED, 0xFE) || Comeca(b, 0xFE, 0xED, 0xFA, 0xCF) || Comeca(b, 0xCA, 0xFE, 0xBA, 0xBE);

    /// <summary>Nome para exibição/download: sem caminho, sem caracteres de controle ou que quebrem cabeçalhos.</summary>
    public static string NomeSeguro(string? nome)
    {
        var n = Path.GetFileName((nome ?? "").Replace('\\', '/').Split('/').Last());
        n = new string(n.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or ':' or '|' or '?' or '*')).ToArray()).Trim(' ', '.');
        if (n.Length > 150)
        {
            var ext = Path.GetExtension(n);
            n = n[..(150 - ext.Length)] + ext;
        }
        return string.IsNullOrEmpty(n) ? "arquivo" : n;
    }

    /// <summary>Content-Disposition com nome ASCII + filename* UTF-8 (RFC 6266).</summary>
    public static string ContentDisposition(string nome, bool inline)
    {
        var ascii = new string(NomeSeguro(nome).Select(c => c < 128 ? c : '_').ToArray());
        return $"{(inline ? "inline" : "attachment")}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(NomeSeguro(nome))}";
    }
}
