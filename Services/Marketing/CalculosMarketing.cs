namespace AgenciaOS.Services.Marketing;

/// <summary>
/// Fórmulas dos indicadores derivados. Todas retornam null quando os dados necessários
/// não existem ou quando a base é zero (sem divisão por zero e sem inventar valores).
/// </summary>
public static class CalculosMarketing
{
    /// <summary>(interações / seguidores) × 100</summary>
    public static decimal? EngajamentoPorSeguidores(decimal? interacoes, decimal? seguidores) =>
        Percentual(interacoes, seguidores);

    /// <summary>(interações / alcance) × 100 — metodologia diferente da por seguidores.</summary>
    public static decimal? EngajamentoPorAlcance(decimal? interacoes, decimal? alcance) =>
        Percentual(interacoes, alcance);

    /// <summary>((seguidores atuais − anteriores) / anteriores) × 100</summary>
    public static decimal? CrescimentoSeguidores(decimal? atuais, decimal? anteriores) =>
        Variacao(atuais, anteriores);

    /// <summary>(cliques / impressões) × 100</summary>
    public static decimal? Ctr(decimal? cliques, decimal? impressoes) =>
        Percentual(cliques, impressoes);

    /// <summary>((atual − anterior) / anterior) × 100</summary>
    public static decimal? Variacao(decimal? atual, decimal? anterior)
    {
        if (atual is null || anterior is null || anterior.Value == 0) return null;
        return Math.Round((atual.Value - anterior.Value) / Math.Abs(anterior.Value) * 100m, 2);
    }

    public static decimal? Percentual(decimal? parte, decimal? total)
    {
        if (parte is null || total is null || total.Value == 0) return null;
        return Math.Round(parte.Value / total.Value * 100m, 2);
    }

    public static decimal? Divisao(decimal? a, decimal? b)
    {
        if (a is null || b is null || b.Value == 0) return null;
        return a.Value / b.Value;
    }
}
