using AgenciaOS.Data;
using AgenciaOS.Models;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Services.Armazenamento;

/// <summary>
/// Move os arquivos legados (wwwroot/uploads e wwwroot/brand) para o armazenamento privado.
/// Para cada arquivo: calcula SHA-256, envia, relê do destino e compara o hash. Só então atualiza o banco.
/// O original só é apagado com <c>removerOriginais</c> e depois da verificação.
/// Executar com: dotnet AgenciaOS.dll --migrar-arquivos [--remover-originais]
/// Deve rodar em escopo de sistema (CriarEscopoSistema).
/// </summary>
public class MigracaoArquivosService(ApplicationDbContext db, IArmazenamentoArquivos armazenamento, IWebHostEnvironment env, ILogger<MigracaoArquivosService> logger)
{
    public record Relatorio(int Migrados, int JaMigrados, int Ausentes, int Falhas, int OriginaisRemovidos, int LogosMigrados, List<string> Ocorrencias);

    /// <param name="raizLegado">Pasta que contém uploads/ e brand/ (padrão: wwwroot).</param>
    public async Task<Relatorio> ExecutarAsync(bool removerOriginais, string? raizLegado = null, CancellationToken ct = default)
    {
        var raiz = Path.GetFullPath(raizLegado ?? env.WebRootPath);
        int migrados = 0, ausentes = 0, falhas = 0, removidos = 0, logos = 0;
        var ocorrencias = new List<string>();

        var jaMigrados = await db.Arquivos.CountAsync(a => a.ChaveArmazenamento != null, ct);
        var pendentes = await db.Arquivos.Where(a => a.ChaveArmazenamento == null).OrderBy(a => a.Id).ToListAsync(ct);

        foreach (var arq in pendentes)
        {
            var origem = CaminhoLegado(raiz, arq.Url, "uploads");
            if (origem == null || !File.Exists(origem))
            {
                ausentes++;
                ocorrencias.Add($"Arquivo {arq.Id}: original ausente ({arq.Url}); registro mantido sem alteração.");
                continue;
            }

            try
            {
                var chave = ChavesArmazenamento.ArquivoCliente(arq.AgenciaId, arq.ClienteId, Path.GetExtension(origem), arq.CriadoEm);
                var hash = await CopiarVerificandoAsync(origem, chave, string.IsNullOrEmpty(arq.TipoMime) ? "application/octet-stream" : arq.TipoMime, ct);

                arq.ChaveArmazenamento = chave;
                arq.Provedor = armazenamento.Nome;
                arq.HashSha256 = hash;
                await db.SaveChangesAsync(ct);
                migrados++;

                if (removerOriginais)
                {
                    File.Delete(origem);
                    removidos++;
                }
            }
            catch (Exception e)
            {
                falhas++;
                ocorrencias.Add($"Arquivo {arq.Id}: falha ({e.Message}); original preservado.");
                logger.LogError(e, "Falha ao migrar arquivo {Id}", arq.Id);
            }
        }

        // Logos das agências em /brand/...: copiados para o armazenamento (os originais ficam; podem estar no repositório)
        foreach (var cfg in await db.Configuracoes.ToListAsync(ct))
        {
            foreach (var marca in new[] { "logo", "logo-marca" })
            {
                var url = marca == "logo" ? cfg.LogoUrl : cfg.LogoMarcaUrl;
                var origem = CaminhoLegado(raiz, url, "brand");
                if (origem == null) continue;
                if (!File.Exists(origem)) { ocorrencias.Add($"Logo da agência {cfg.AgenciaId} ausente ({url})."); continue; }
                if (!ValidadorArquivos.ExtensoesImagemMarca.Contains(Path.GetExtension(origem).ToLowerInvariant()))
                {
                    ocorrencias.Add($"Logo da agência {cfg.AgenciaId} em formato não suportado ({url}); envie novamente em PNG/JPG/WEBP.");
                    continue;
                }
                try
                {
                    var ext = Path.GetExtension(origem).ToLowerInvariant();
                    var nome = $"{marca}-{Guid.NewGuid():N}{ext}";
                    await CopiarVerificandoAsync(origem, ChavesArmazenamento.Marca(cfg.AgenciaId, nome), MimeImagem(ext), ct);
                    var nova = $"/marca/{cfg.AgenciaId}/{nome}";
                    if (marca == "logo") cfg.LogoUrl = nova; else cfg.LogoMarcaUrl = nova;
                    await db.SaveChangesAsync(ct);
                    logos++;
                }
                catch (Exception e)
                {
                    falhas++;
                    ocorrencias.Add($"Logo da agência {cfg.AgenciaId}: falha ({e.Message}).");
                }
            }
        }

        var rel = new Relatorio(migrados, jaMigrados, ausentes, falhas, removidos, logos, ocorrencias);
        logger.LogInformation("Migração de arquivos: {Migrados} migrados, {Ja} já migrados, {Ausentes} ausentes, {Falhas} falhas, {Removidos} originais removidos, {Logos} logos",
            migrados, jaMigrados, ausentes, falhas, removidos, logos);
        foreach (var o in ocorrencias) logger.LogWarning("{Ocorrencia}", o);
        return rel;
    }

    private async Task<string> CopiarVerificandoAsync(string origem, string chave, string mime, CancellationToken ct)
    {
        string hashOrigem;
        await using (var fs = File.OpenRead(origem))
        {
            hashOrigem = await ServicoArquivos.HashAsync(fs, ct);
            fs.Position = 0;
            await armazenamento.SalvarAsync(chave, fs, mime, ct);
        }

        await using var destino = await armazenamento.AbrirAsync(chave, ct)
            ?? throw new IOException("Objeto não encontrado no destino após o envio.");
        var hashDestino = await ServicoArquivos.HashAsync(destino, ct);
        if (hashDestino != hashOrigem)
        {
            await armazenamento.ExcluirAsync(chave, ct);
            throw new IOException("Hash do destino difere do original.");
        }
        return hashOrigem;
    }

    /// <summary>Converte "/uploads/1/x.pdf" em caminho absoluto, garantindo que fica dentro de raiz/{pasta}.</summary>
    private static string? CaminhoLegado(string raiz, string? url, string pasta)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith($"/{pasta}/", StringComparison.OrdinalIgnoreCase)) return null;
        var permitido = Path.GetFullPath(Path.Combine(raiz, pasta)) + Path.DirectorySeparatorChar;
        var caminho = Path.GetFullPath(Path.Combine(raiz, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        return caminho.StartsWith(permitido, StringComparison.OrdinalIgnoreCase) ? caminho : null;
    }

    private static string MimeImagem(string ext) => ext switch
    {
        ".png" => "image/png", ".webp" => "image/webp", ".jpg" or ".jpeg" => "image/jpeg", ".svg" => "image/svg+xml", _ => "application/octet-stream"
    };
}
