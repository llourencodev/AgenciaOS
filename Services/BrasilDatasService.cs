using AgenciaOS.Models;

namespace AgenciaOS.Services;

public static class BrasilDatasService
{
    /// <summary>
    /// Retorna todas as datas comemorativas do Brasil para o ano informado.
    /// Inclui feriados nacionais fixos, datas variáveis (Páscoa, Carnaval, etc.)
    /// e principais datas comerciais/marketing.
    /// </summary>
    public static List<DataComemorativa> ObterParaAno(int ano)
    {
        var pascoa = CalcularPascoa(ano);
        var datas  = new List<DataComemorativa>();

        // ── FERIADOS NACIONAIS FIXOS ────────────────────────────────────────
        datas.Add(Criar(1,  1,  "Confraternização Universal (Ano Novo)",  "#f59e0b", TipoDataComemorativa.Nacional));
        datas.Add(Criar(21, 4,  "Tiradentes",                             "#ef4444", TipoDataComemorativa.Nacional));
        datas.Add(Criar(1,  5,  "Dia do Trabalho",                        "#ef4444", TipoDataComemorativa.Nacional));
        datas.Add(Criar(7,  9,  "Independência do Brasil",                "#22c55e", TipoDataComemorativa.Nacional));
        datas.Add(Criar(12, 10, "Nossa Senhora Aparecida",                "#6366f1", TipoDataComemorativa.Nacional));
        datas.Add(Criar(2,  11, "Finados",                                "#64748b", TipoDataComemorativa.Nacional));
        datas.Add(Criar(15, 11, "Proclamação da República",               "#22c55e", TipoDataComemorativa.Nacional));
        datas.Add(Criar(20, 11, "Consciência Negra",                      "#92400e", TipoDataComemorativa.Nacional));
        datas.Add(Criar(25, 12, "Natal",                                  "#ef4444", TipoDataComemorativa.Nacional, "A data mais importante para campanhas de fim de ano."));

        // ── DATAS VARIÁVEIS (baseadas na Páscoa) ────────────────────────────
        var carnaval      = pascoa.AddDays(-47);
        var sextaSanta    = pascoa.AddDays(-2);
        var corpusChristi = pascoa.AddDays(60);

        datas.Add(CriarVariavel(carnaval.Day,      carnaval.Month,      ano, "Carnaval",            "#f59e0b", TipoDataComemorativa.Nacional, "Alta oportunidade de engajamento em redes sociais."));
        datas.Add(CriarVariavel(sextaSanta.Day,    sextaSanta.Month,    ano, "Sexta-feira Santa",   "#64748b", TipoDataComemorativa.Nacional));
        datas.Add(CriarVariavel(pascoa.Day,        pascoa.Month,        ano, "Páscoa",              "#a78bfa", TipoDataComemorativa.Comercial, "Datas com forte apelo para marcas de alimentos, família e presentes."));
        datas.Add(CriarVariavel(corpusChristi.Day, corpusChristi.Month, ano, "Corpus Christi",      "#64748b", TipoDataComemorativa.Nacional));

        // ── DATAS COMERCIAIS / MARKETING ────────────────────────────────────
        datas.Add(Criar(8,  3,  "Dia Internacional da Mulher",           "#ec4899", TipoDataComemorativa.Marketing, "Excelente para campanhas de empoderamento e produtos femininos."));
        datas.Add(Criar(14, 2,  "Dia de São Valentim",                   "#f43f5e", TipoDataComemorativa.Comercial, "Muito usado por marcas internacionais no Brasil."));
        datas.Add(Criar(15, 3,  "Dia do Consumidor",                     "#3b82f6", TipoDataComemorativa.Marketing, "Promoções e ações de fidelização de clientes."));
        datas.Add(Criar(1,  4,  "Dia da Mentira",                        "#f97316", TipoDataComemorativa.Marketing, "Bom para campanhas criativas e bem-humoradas."));
        datas.Add(Criar(22, 4,  "Dia da Terra",                          "#22c55e", TipoDataComemorativa.Marketing, "Sustentabilidade e responsabilidade ambiental."));
        datas.Add(Criar(12, 6,  "Dia dos Namorados",                     "#f43f5e", TipoDataComemorativa.Comercial, "Segunda data mais importante para o comércio brasileiro."));
        datas.Add(Criar(12, 7,  "Dia do Rock",                           "#8b5cf6", TipoDataComemorativa.Marketing));
        datas.Add(Criar(25, 7,  "Dia do Motorista",                      "#f59e0b", TipoDataComemorativa.Comercial));
        datas.Add(Criar(9,  8,  "Dia dos Povos Indígenas",               "#a16207", TipoDataComemorativa.Nacional));
        datas.Add(Criar(12, 10, "Dia das Crianças",                      "#f59e0b", TipoDataComemorativa.Comercial, "Terceira data mais importante para o varejo. Produtos infantis em alta."));
        datas.Add(Criar(31, 10, "Halloween",                             "#f97316", TipoDataComemorativa.Marketing, "Crescente no Brasil, especialmente em marcas voltadas ao público jovem."));
        datas.Add(Criar(2,  12, "Dia do Samba",                          "#f59e0b", TipoDataComemorativa.Regional));

        // ── DIA DAS MÃES — 2° domingo de maio ───────────────────────────────
        var diasMaes = SegundoDomingo(ano, 5);
        datas.Add(CriarVariavel(diasMaes.Day, diasMaes.Month, ano, "Dia das Mães",
            "#ec4899", TipoDataComemorativa.Comercial, "A maior data do comércio brasileiro. Planeje campanhas com antecedência."));

        // ── DIA DOS PAIS — 2° domingo de agosto ─────────────────────────────
        var diasPais = SegundoDomingo(ano, 8);
        datas.Add(CriarVariavel(diasPais.Day, diasPais.Month, ano, "Dia dos Pais",
            "#3b82f6", TipoDataComemorativa.Comercial, "Segunda data mais importante de presentes do ano."));

        // ── BLACK FRIDAY — última sexta de novembro ──────────────────────────
        var blackFriday = UltimaSemanaDo(ano, 11, DayOfWeek.Friday);
        datas.Add(CriarVariavel(blackFriday.Day, blackFriday.Month, ano, "Black Friday",
            "#1e293b", TipoDataComemorativa.Comercial, "Planeje campanhas de antecipação. Maior volume de vendas online do ano."));

        // ── CYBER MONDAY — segunda após Black Friday ─────────────────────────
        var cyberMonday = blackFriday.AddDays(3);
        datas.Add(CriarVariavel(cyberMonday.Day, cyberMonday.Month, ano, "Cyber Monday",
            "#6366f1", TipoDataComemorativa.Comercial, "Foco em e-commerce e tecnologia."));

        return datas.OrderBy(d => d.Mes).ThenBy(d => d.Dia).ToList();
    }

    // ── HELPERS ─────────────────────────────────────────────────────────────

    private static DataComemorativa Criar(int dia, int mes, string nome, string cor,
        TipoDataComemorativa tipo, string? descricao = null) => new()
    {
        Dia       = dia,
        Mes       = mes,
        Nome      = nome,
        Cor       = cor,
        Tipo      = tipo,
        Anual     = true,
        Descricao = descricao,
        CriadoEm  = DateTime.UtcNow
    };

    private static DataComemorativa CriarVariavel(int dia, int mes, int ano, string nome, string cor,
        TipoDataComemorativa tipo, string? descricao = null) => new()
    {
        Dia       = dia,
        Mes       = mes,
        Ano       = ano,
        Nome      = nome,
        Cor       = cor,
        Tipo      = tipo,
        Anual     = false,
        Descricao = descricao,
        CriadoEm  = DateTime.UtcNow
    };

    /// <summary>Algoritmo de Meeus/Jones/Butcher para calcular a Páscoa.</summary>
    private static DateTime CalcularPascoa(int ano)
    {
        int a = ano % 19;
        int b = ano / 100;
        int c = ano % 100;
        int d = b / 4;
        int e = b % 4;
        int f = (b + 8) / 25;
        int g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4;
        int k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day   = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateTime(ano, month, day);
    }

    /// <summary>Retorna o segundo domingo de um mês.</summary>
    private static DateTime SegundoDomingo(int ano, int mes)
    {
        var primeiroDia = new DateTime(ano, mes, 1);
        var primeiroDomingo = primeiroDia.AddDays((7 - (int)primeiroDia.DayOfWeek) % 7);
        return primeiroDomingo.AddDays(7);
    }

    /// <summary>Retorna a última ocorrência de um dia da semana em um mês.</summary>
    private static DateTime UltimaSemanaDo(int ano, int mes, DayOfWeek diaSemana)
    {
        var ultimoDia = new DateTime(ano, mes, DateTime.DaysInMonth(ano, mes));
        var diff = ((int)ultimoDia.DayOfWeek - (int)diaSemana + 7) % 7;
        return ultimoDia.AddDays(-diff);
    }
}
