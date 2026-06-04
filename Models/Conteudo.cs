namespace AgenciaOS.Models;

public class Conteudo
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descricao { get; set; }
    public string? Legenda { get; set; }
    public TipoConteudo Tipo { get; set; }
    public StatusConteudo Status { get; set; } = StatusConteudo.PautaCriada;
    public DateTime DataPrevista { get; set; }
    public DateTime? DataPublicacao { get; set; }
    public string? ArquivoUrl { get; set; }
    public string? Hashtags { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public int ClienteId { get; set; }
    public Cliente Cliente { get; set; } = null!;

    public string? CriadoPorId { get; set; }
    public Usuario? CriadoPor { get; set; }

    public ICollection<ComentarioConteudo> Comentarios { get; set; } = [];
    public ICollection<HistoricoConteudo> Historicos { get; set; } = [];
}

public enum TipoConteudo
{
    Reels,
    Story,
    Carrossel,
    Anuncio,
    Institucional,
    Feed
}

public enum StatusConteudo
{
    PautaCriada,
    AguardandoGravacao,
    AguardandoEdicao,
    RevisaoInterna,
    AguardandoAprovacao,
    Aprovado,
    AjusteSolicitado,
    Publicado
}

public class ComentarioConteudo
{
    public int Id { get; set; }
    public string Texto { get; set; } = "";
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public int ConteudoId { get; set; }
    public Conteudo Conteudo { get; set; } = null!;
    public string UsuarioId { get; set; } = "";
    public Usuario Usuario { get; set; } = null!;
}

public class HistoricoConteudo
{
    public int Id { get; set; }
    public string Descricao { get; set; } = "";
    public StatusConteudo StatusAnterior { get; set; }
    public StatusConteudo StatusNovo { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public int ConteudoId { get; set; }
    public Conteudo Conteudo { get; set; } = null!;
    public string UsuarioId { get; set; } = "";
    public Usuario Usuario { get; set; } = null!;
}
