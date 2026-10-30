namespace AgenciaOS.Services.Marketing;

/// <summary>Período de análise (datas inclusivas) e o período anterior equivalente.</summary>
public record PeriodoMarketing(DateOnly Inicio, DateOnly Fim, string Codigo, string Rotulo)
{
    public static readonly (string Codigo, string Rotulo)[] Opcoes =
    [
        ("hoje", "Hoje"),
        ("7d", "Últimos 7 dias"),
        ("15d", "Últimos 15 dias"),
        ("30d", "Últimos 30 dias"),
        ("90d", "Últimos 90 dias"),
        ("mes", "Este mês"),
        ("mes_anterior", "Mês anterior"),
        ("personalizado", "Período personalizado"),
    ];

    public const int MaxDias = 400;

    public int Dias => Fim.DayNumber - Inicio.DayNumber + 1;

    public static PeriodoMarketing Criar(string? codigo, DateOnly? de, DateOnly? ate, DateOnly hoje)
    {
        codigo = string.IsNullOrWhiteSpace(codigo) ? "30d" : codigo;
        PeriodoMarketing UltimosDias(int n, string c, string r) => new(hoje.AddDays(-(n - 1)), hoje, c, r);

        switch (codigo)
        {
            case "hoje": return new(hoje, hoje, codigo, "Hoje");
            case "7d": return UltimosDias(7, codigo, "Últimos 7 dias");
            case "15d": return UltimosDias(15, codigo, "Últimos 15 dias");
            case "90d": return UltimosDias(90, codigo, "Últimos 90 dias");
            case "mes": return new(new DateOnly(hoje.Year, hoje.Month, 1), hoje, codigo, "Este mês");
            case "mes_anterior":
                var inicio = new DateOnly(hoje.Year, hoje.Month, 1).AddMonths(-1);
                return new(inicio, inicio.AddMonths(1).AddDays(-1), codigo, "Mês anterior");
            case "personalizado" when de is not null && ate is not null:
                var (a, b) = de <= ate ? (de.Value, ate.Value) : (ate.Value, de.Value);
                if (b > hoje) b = hoje;
                if (a > b) a = b;
                if (b.DayNumber - a.DayNumber + 1 > MaxDias) a = b.AddDays(-(MaxDias - 1));
                return new(a, b, codigo, $"{a:dd/MM/yyyy} a {b:dd/MM/yyyy}");
            default: return UltimosDias(30, "30d", "Últimos 30 dias");
        }
    }

    /// <summary>
    /// Período anterior equivalente: o mês inteiro anterior para "mês anterior"; para "este mês", os mesmos dias corridos do mês anterior
    /// (limitado ao tamanho dele); nos demais, o mesmo número de dias imediatamente antes.
    /// </summary>
    public PeriodoMarketing Anterior()
    {
        if (Codigo is "mes" or "mes_anterior")
        {
            var inicio = Inicio.AddMonths(-1);
            var fimMes = Inicio.AddDays(-1);
            var fim = Codigo == "mes_anterior" ? fimMes : inicio.AddDays(Dias - 1);
            if (fim > fimMes) fim = fimMes;
            return new(inicio, fim, Codigo, "Período anterior");
        }
        return new(Inicio.AddDays(-Dias), Inicio.AddDays(-1), Codigo, "Período anterior");
    }
}
