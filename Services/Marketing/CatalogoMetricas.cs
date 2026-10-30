using AgenciaOS.Models;

namespace AgenciaOS.Services.Marketing;

public enum AgregacaoMetrica
{
    /// <summary>Valores diários somados no período (ex.: interações, sessões).</summary>
    Soma,
    /// <summary>Snapshot: vale o último valor do período (ex.: seguidores).</summary>
    Ultimo
}

public record DefinicaoMetrica(string Chave, string Nome, AgregacaoMetrica Agregacao, string? Observacao = null);

/// <summary>
/// Chaves normalizadas de métricas guardadas em MetricaDiaria / MetricaDimensao.
/// Cada provedor converte os nomes da API para estas chaves.
/// </summary>
public static class Metricas
{
    // Redes sociais
    public const string Seguidores        = "seguidores";
    public const string NovosSeguidores   = "novos_seguidores";
    public const string Alcance           = "alcance";
    public const string Impressoes        = "impressoes";
    public const string Visualizacoes     = "visualizacoes";
    public const string Interacoes        = "interacoes";
    public const string ContasEngajadas   = "contas_engajadas";
    public const string Curtidas          = "curtidas";
    public const string Comentarios       = "comentarios";
    public const string Compartilhamentos = "compartilhamentos";
    public const string Salvamentos       = "salvamentos";
    public const string VisitasPerfil     = "visitas_perfil";
    public const string Cliques           = "cliques";
    public const string CliquesLink       = "cliques_link";
    public const string VisualizacoesVideo = "visualizacoes_video";
    public const string Publicacoes       = "publicacoes";
    public const string CurtidasTotais    = "curtidas_totais";

    // Google Analytics 4
    public const string UsuariosAtivos     = "usuarios_ativos";
    public const string NovosUsuarios      = "novos_usuarios";
    public const string Sessoes            = "sessoes";
    public const string SessoesEngajadas   = "sessoes_engajadas";
    public const string VisualizacoesPagina = "visualizacoes_pagina";
    public const string TempoEngajamento   = "tempo_engajamento_seg";
    public const string Eventos            = "eventos";
    public const string EventosPrincipais  = "eventos_principais";

    // Search Console
    public const string CliquesOrganicos    = "cliques_organicos";
    public const string ImpressoesOrganicas = "impressoes_organicas";
    /// <summary>posição × impressões, para calcular a posição média ponderada.</summary>
    public const string PosicaoPonderada    = "posicao_x_impressoes";

    public static readonly IReadOnlyDictionary<string, DefinicaoMetrica> Definicoes = new[]
    {
        new DefinicaoMetrica(Seguidores, "Seguidores", AgregacaoMetrica.Ultimo),
        new DefinicaoMetrica(NovosSeguidores, "Novos seguidores", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Alcance, "Alcance", AgregacaoMetrica.Soma, "Soma do alcance diário: uma mesma pessoa alcançada em dias diferentes é contada mais de uma vez."),
        new DefinicaoMetrica(Impressoes, "Impressões", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Visualizacoes, "Visualizações", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Interacoes, "Interações", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(ContasEngajadas, "Contas engajadas", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Curtidas, "Curtidas", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Comentarios, "Comentários", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Compartilhamentos, "Compartilhamentos", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Salvamentos, "Salvamentos", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(VisitasPerfil, "Visitas ao perfil", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Cliques, "Cliques", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(CliquesLink, "Toques em links", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(VisualizacoesVideo, "Visualizações de vídeo", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Publicacoes, "Publicações", AgregacaoMetrica.Ultimo),
        new DefinicaoMetrica(CurtidasTotais, "Curtidas totais", AgregacaoMetrica.Ultimo),
        new DefinicaoMetrica(UsuariosAtivos, "Usuários ativos", AgregacaoMetrica.Soma, "Soma dos usuários ativos de cada dia: o mesmo visitante em dias diferentes é contado mais de uma vez."),
        new DefinicaoMetrica(NovosUsuarios, "Novos usuários", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Sessoes, "Sessões", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(SessoesEngajadas, "Sessões engajadas", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(VisualizacoesPagina, "Visualizações de página", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(TempoEngajamento, "Tempo de engajamento (s)", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(Eventos, "Eventos", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(EventosPrincipais, "Eventos principais (conversões)", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(CliquesOrganicos, "Cliques orgânicos", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(ImpressoesOrganicas, "Impressões na pesquisa", AgregacaoMetrica.Soma),
        new DefinicaoMetrica(PosicaoPonderada, "Posição × impressões", AgregacaoMetrica.Soma),
    }.ToDictionary(d => d.Chave);

    public static bool EhSnapshot(string chave) =>
        Definicoes.TryGetValue(chave, out var d) && d.Agregacao == AgregacaoMetrica.Ultimo;

    public static string Nome(string chave) => Definicoes.TryGetValue(chave, out var d) ? d.Nome : chave;
}

public static class PlataformaInfo
{
    public static readonly PlataformaMarketing[] Sociais =
        [PlataformaMarketing.Instagram, PlataformaMarketing.Facebook, PlataformaMarketing.TikTok];

    public static string Nome(PlataformaMarketing p) => p switch
    {
        PlataformaMarketing.Instagram       => "Instagram",
        PlataformaMarketing.Facebook        => "Facebook",
        PlataformaMarketing.TikTok          => "TikTok",
        PlataformaMarketing.GoogleAnalytics => "Google Analytics 4",
        PlataformaMarketing.SearchConsole   => "Google Search Console",
        _ => p.ToString()
    };

    public static string Icone(PlataformaMarketing p) => p switch
    {
        PlataformaMarketing.Instagram       => "bi-instagram",
        PlataformaMarketing.Facebook        => "bi-facebook",
        PlataformaMarketing.TikTok          => "bi-tiktok",
        PlataformaMarketing.GoogleAnalytics => "bi-graph-up",
        PlataformaMarketing.SearchConsole   => "bi-search",
        _ => "bi-globe"
    };

    public static string Cor(PlataformaMarketing p) => p switch
    {
        PlataformaMarketing.Instagram       => "#d62976",
        PlataformaMarketing.Facebook        => "#1877f2",
        PlataformaMarketing.TikTok          => "#111111",
        PlataformaMarketing.GoogleAnalytics => "#e37400",
        PlataformaMarketing.SearchConsole   => "#34a853",
        _ => "#6366f1"
    };

    public static string Descricao(PlataformaMarketing p) => p switch
    {
        PlataformaMarketing.Instagram       => "Perfil profissional (comercial ou criador): seguidores, alcance, interações e desempenho das publicações.",
        PlataformaMarketing.Facebook        => "Páginas que você administra: seguidores, alcance, engajamento e publicações.",
        PlataformaMarketing.TikTok          => "Perfil, seguidores, curtidas e desempenho dos vídeos recentes.",
        PlataformaMarketing.GoogleAnalytics => "Tráfego do site: usuários, sessões, origens, páginas, dispositivos e conversões.",
        PlataformaMarketing.SearchConsole   => "SEO: cliques orgânicos, impressões, CTR, posição média e palavras-chave.",
        _ => ""
    };

    /// <summary>Permite vincular mais de uma conta/propriedade numa mesma conexão.</summary>
    public static bool PermiteMultiplas(PlataformaMarketing p) =>
        p is PlataformaMarketing.Facebook or PlataformaMarketing.GoogleAnalytics or PlataformaMarketing.SearchConsole;

    public static string Slug(PlataformaMarketing p) => p.ToString().ToLowerInvariant();

    public static bool TentarSlug(string? slug, out PlataformaMarketing p) =>
        Enum.TryParse(slug, true, out p) && Enum.IsDefined(p);
}
