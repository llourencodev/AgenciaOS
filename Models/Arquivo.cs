namespace AgenciaOS.Models;

public class Arquivo : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public string Nome { get; set; } = "";
    public string NomeOriginal { get; set; } = "";
    /// <summary>Legado: caminho público em wwwroot/uploads (antes da Fase 2). Arquivos novos usam ChaveArmazenamento.</summary>
    public string Url { get; set; } = "";
    /// <summary>Chave no armazenamento privado: agencies/{agenciaId}/clients/{clienteId}/...</summary>
    public string? ChaveArmazenamento { get; set; }
    public string? Provedor { get; set; }
    public string? HashSha256 { get; set; }
    public string? Thumbnail { get; set; }
    public long TamanhoBytes { get; set; }
    public string TipoMime { get; set; } = "";
    public CategoriaArquivo Categoria { get; set; }
    public string? MesReferencia { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public int ClienteId { get; set; }
    [Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever]
    public Cliente Cliente { get; set; } = null!;

    public string? UploadPorId { get; set; }
    public Usuario? UploadPor { get; set; }

    public string TamanhoFormatado => TamanhoBytes switch
    {
        < 1024 => $"{TamanhoBytes} B",
        < 1048576 => $"{TamanhoBytes / 1024.0:F1} KB",
        < 1073741824 => $"{TamanhoBytes / 1048576.0:F1} MB",
        _ => $"{TamanhoBytes / 1073741824.0:F1} GB"
    };

    public bool EImagem => TipoMime.StartsWith("image/");
    public bool EVideo => TipoMime.StartsWith("video/");
}

public enum CategoriaArquivo { Bruto, Editado, Aprovado, Publicado }
