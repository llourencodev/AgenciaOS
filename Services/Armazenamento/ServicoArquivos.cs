using System.Security.Cryptography;

namespace AgenciaOS.Services.Armazenamento;

/// <summary>
/// Recebe um upload, valida, calcula SHA-256 e grava no armazenamento privado.
/// Quem chama é responsável por verificar se o usuário pode gravar naquele cliente/agência.
/// </summary>
public class ServicoArquivos(IArmazenamentoArquivos armazenamento, ValidadorArquivos validador)
{
    public record Gravado(string Chave, string Provedor, string HashSha256, string Mime, long Tamanho, string NomeSeguro);
    public record Resultado(Gravado? Arquivo, string? Erro);

    public IArmazenamentoArquivos Armazenamento => armazenamento;

    public Task<Resultado> GravarAsync(IFormFile arquivo, Func<string, string> gerarChave,
        IReadOnlyCollection<string>? extensoesPermitidas = null, long? tamanhoMaximo = null, CancellationToken ct = default)
    {
        if (arquivo == null || arquivo.Length == 0) return Task.FromResult(new Resultado(null, "Selecione um arquivo."));
        return GravarAsync(arquivo.OpenReadStream(), arquivo.FileName, arquivo.Length, gerarChave, extensoesPermitidas, tamanhoMaximo, ct);
    }

    /// <param name="gerarChave">Recebe a extensão validada e devolve a chave de armazenamento.</param>
    public async Task<Resultado> GravarAsync(Stream origem, string nomeOriginal, long tamanho, Func<string, string> gerarChave,
        IReadOnlyCollection<string>? extensoesPermitidas = null, long? tamanhoMaximo = null, CancellationToken ct = default)
    {
        var max = tamanhoMaximo ?? validador.TamanhoMaximo;
        if (tamanho > max) return new(null, $"Arquivo muito grande. Limite: {max / 1024 / 1024} MB.");

        // Cópia temporária: permite ler o cabeçalho, calcular o hash e reenviar com tamanho conhecido
        await using var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await using (origem) await CopiarComLimiteAsync(origem, temp, max, ct);
        if (temp.Length > max) return new(null, $"Arquivo muito grande. Limite: {max / 1024 / 1024} MB.");

        temp.Position = 0;
        var cabecalho = new byte[(int)Math.Min(8192, temp.Length)];
        await temp.ReadExactlyAsync(cabecalho, ct);
        var v = validador.Validar(nomeOriginal, temp.Length, cabecalho, extensoesPermitidas, max);
        if (!v.Valido) return new(null, v.Erro);

        temp.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(temp, ct));

        var chave = gerarChave(v.Extensao);
        temp.Position = 0;
        await armazenamento.SalvarAsync(chave, temp, v.Mime, ct);
        return new(new Gravado(chave, armazenamento.Nome, hash, v.Mime, temp.Length, v.NomeSeguro), null);
    }

    private static async Task CopiarComLimiteAsync(Stream origem, Stream destino, long max, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int lidos;
        while ((lidos = await origem.ReadAsync(buffer, ct)) > 0)
        {
            total += lidos;
            await destino.WriteAsync(buffer.AsMemory(0, lidos), ct);
            if (total > max) return; // o chamador recusa pelo tamanho
        }
    }

    public static async Task<string> HashAsync(Stream s, CancellationToken ct = default) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
}
