using AgenciaOS.Models;
using AgenciaOS.Services.Marketing;

namespace AgenciaOS.ViewModels;

public class FiltroMarketing
{
    public int? ClienteId { get; set; }
    public PlataformaMarketing? Plataforma { get; set; }
    public int? ContaId { get; set; }
    public string? Periodo { get; set; } = "30d";
    public DateOnly? De { get; set; }
    public DateOnly? Ate { get; set; }
    public string? Aba { get; set; }
    public string? Tipo { get; set; }
    public string? Ordem { get; set; }
    public int Pagina { get; set; } = 1;
}

public enum FormatoKpi { Numero, Percentual, Segundos, Posicao }

public class FonteKpi
{
    public PlataformaMarketing Plataforma { get; init; }
    public string Conta { get; init; } = "";
    public decimal? Valor { get; init; }
    public decimal? Anterior { get; init; }
    public decimal? Variacao => CalculosMarketing.Variacao(Valor, Anterior);
    public string? Observacao { get; init; }
}

public class KpiMarketing
{
    public string Chave { get; init; } = "";
    public string Titulo { get; init; } = "";
    public string Icone { get; init; } = "bi-graph-up";
    public string Cor { get; init; } = "indigo";
    public FormatoKpi Formato { get; init; }
    public decimal? Valor { get; set; }
    public decimal? Anterior { get; set; }
    public decimal? Variacao => Formato == FormatoKpi.Posicao
        ? CalculosMarketing.Variacao(Anterior, Valor) // posição: menor é melhor
        : CalculosMarketing.Variacao(Valor, Anterior);
    public List<FonteKpi> Fontes { get; } = [];
    public string? Metodologia { get; set; }
    /// <summary>Preenchido quando não há total porque as fontes não são comparáveis.</summary>
    public string? SemTotal { get; set; }
    public string? Indisponivel { get; set; }
    public DateTime? UltimaAtualizacao { get; set; }
    public string FontesTexto => string.Join(", ", Fontes.Select(f => PlataformaInfo.Nome(f.Plataforma)).Distinct());
}

public class SerieGrafico
{
    public string Rotulo { get; init; } = "";
    public string Cor { get; init; } = "#6366f1";
    public List<decimal?> Valores { get; init; } = [];
    public string? Eixo { get; init; }
}

public class GraficoMarketing
{
    public string Id { get; init; } = "";
    public string Titulo { get; init; } = "";
    public string Tipo { get; init; } = "line";
    public List<string> Rotulos { get; init; } = [];
    public List<SerieGrafico> Series { get; init; } = [];
    public string? Nota { get; init; }
    public bool Vazio => Series.All(s => s.Valores.All(v => v is null or 0));
}

public class TabelaMarketing
{
    public string Titulo { get; init; } = "";
    public string[] Colunas { get; init; } = [];
    public List<string[]> Linhas { get; init; } = [];
    public string? Nota { get; init; }
}

public class ContaResumo
{
    public int Id { get; init; }
    public int ClienteId { get; init; }
    public string Cliente { get; init; } = "";
    public PlataformaMarketing Plataforma { get; init; }
    public string Nome { get; init; } = "";
    public string? NomeUsuario { get; init; }
    public string? FotoUrl { get; init; }
    public StatusIntegracao Status { get; init; }
    public bool Ativa { get; init; }
    public DateTime? UltimaSincronizacao { get; init; }
    public string? UltimoErro { get; init; }
    public string? Limitacao { get; init; }
    public string[] Indisponiveis { get; init; } = [];
}

public class PainelMarketingViewModel
{
    public FiltroMarketing Filtro { get; init; } = new();
    public PeriodoMarketing Periodo { get; init; } = null!;
    public PeriodoMarketing PeriodoAnterior { get; init; } = null!;
    public bool EhAgencia { get; init; }
    public List<Cliente> Clientes { get; init; } = [];
    public List<ContaResumo> Contas { get; init; } = [];
    public List<KpiMarketing> Kpis { get; init; } = [];
    public List<GraficoMarketing> Graficos { get; init; } = [];
    public List<TabelaMarketing> Tabelas { get; init; } = [];
    public List<PublicacaoMarketing> MelhoresPublicacoes { get; init; } = [];
    public List<string> Avisos { get; init; } = [];
    public DateTime? UltimaAtualizacao => Contas.Max(c => c.UltimaSincronizacao);
}

public class ConteudoMarketingViewModel
{
    public FiltroMarketing Filtro { get; init; } = new();
    public PeriodoMarketing Periodo { get; init; } = null!;
    public bool EhAgencia { get; init; }
    public List<Cliente> Clientes { get; init; } = [];
    public List<ContaResumo> Contas { get; init; } = [];
    public List<PublicacaoMarketing> Publicacoes { get; init; } = [];
    public List<PublicacaoMarketing> Melhores { get; init; } = [];
    public Dictionary<int, decimal> SeguidoresPorConta { get; init; } = [];
    public List<string> Tipos { get; init; } = [];
    public int Total { get; init; }
    public int TamanhoPagina { get; init; } = 24;
}

public class InsightMarketing
{
    public string Titulo { get; init; } = "";
    public string Texto { get; init; } = "";
    /// <summary>positivo | negativo | neutro</summary>
    public string Tom { get; init; } = "neutro";
    public string Icone { get; init; } = "bi-lightbulb";
    public string? Fonte { get; init; }
}

public class InsightsViewModel
{
    public FiltroMarketing Filtro { get; init; } = new();
    public PeriodoMarketing Periodo { get; init; } = null!;
    public bool EhAgencia { get; init; }
    public List<Cliente> Clientes { get; init; } = [];
    public List<InsightMarketing> Insights { get; init; } = [];
}

public class ClienteCarteira
{
    public Cliente Cliente { get; init; } = null!;
    public List<ContaResumo> Contas { get; init; } = [];
    public int Problemas => Contas.Count(c => c.Ativa && c.Status is StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada or StatusIntegracao.ErroSincronizacao);
    public DateTime? UltimaSincronizacao => Contas.Max(c => c.UltimaSincronizacao);
    public decimal? Seguidores { get; set; }
    public decimal? Interacoes { get; set; }
    public decimal? Sessoes { get; set; }
    public decimal? CliquesOrganicos { get; set; }
}

public class CarteiraViewModel
{
    public string? Busca { get; init; }
    public string? Situacao { get; init; }
    public PlataformaMarketing? Plataforma { get; init; }
    public List<ClienteCarteira> Clientes { get; init; } = [];
    public List<HistoricoSincronizacao> Historico { get; init; } = [];
    public Dictionary<int, string> NomesClientes { get; init; } = [];
}

public class CardIntegracao
{
    public PlataformaMarketing Plataforma { get; init; }
    public ConexaoMarketing? Conexao { get; init; }
    public List<ContaResumo> Contas { get; init; } = [];
    public string[] VariaveisPendentes { get; init; } = [];
    public StatusIntegracao Status { get; init; }
    public string? Erro { get; init; }
    public DateTime? UltimaSincronizacao => Contas.Where(c => c.Ativa).Max(c => c.UltimaSincronizacao);
}

public class IntegracoesViewModel
{
    public Cliente? Cliente { get; init; }
    public bool EhAgencia { get; init; }
    public List<Cliente> Clientes { get; init; } = [];
    public List<CardIntegracao> Cards { get; init; } = [];
    public string RedirectBase { get; init; } = "";
}

public class SelecionarContasViewModel
{
    public int ConexaoId { get; init; }
    public Cliente Cliente { get; init; } = null!;
    public PlataformaMarketing Plataforma { get; init; }
    public List<AgenciaOS.Services.Marketing.Provedores.ContaDisponivel> Disponiveis { get; init; } = [];
    public HashSet<string> JaVinculadas { get; init; } = [];
    public string? Erro { get; init; }
}

public class RelatoriosViewModel
{
    public bool EhAgencia { get; init; }
    public List<Cliente> Clientes { get; init; } = [];
    public List<RelatorioMarketing> Gerados { get; init; } = [];
}

public class FiltrosMarketingPartial
{
    public FiltroMarketing Filtro { get; init; } = new();
    public List<Cliente> Clientes { get; init; } = [];
    public List<ContaResumo> Contas { get; init; } = [];
    public bool EhAgencia { get; init; }
    public string Acao { get; init; } = "Index";
    public bool MostrarPlataforma { get; init; } = true;
    public bool MostrarConta { get; init; } = true;
    public string? Extra { get; init; }
}
