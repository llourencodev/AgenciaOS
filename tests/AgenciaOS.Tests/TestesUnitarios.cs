using System.Net;
using System.Text.Json;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing;
using AgenciaOS.Services.Marketing.Provedores;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace AgenciaOS.Tests;

public class CalculosTests
{
    [Fact]
    public void Formulas_sem_divisao_por_zero()
    {
        Assert.Null(CalculosMarketing.Variacao(10, 0));
        Assert.Null(CalculosMarketing.Variacao(10, null));
        Assert.Null(CalculosMarketing.Ctr(5, 0));
        Assert.Null(CalculosMarketing.EngajamentoPorSeguidores(5, null));
        Assert.Null(CalculosMarketing.CrescimentoSeguidores(100, 0));
    }

    [Fact]
    public void Formulas_conforme_especificacao()
    {
        Assert.Equal(50m, CalculosMarketing.Variacao(150, 100));
        Assert.Equal(-25m, CalculosMarketing.Variacao(75, 100));
        Assert.Equal(2.5m, CalculosMarketing.Ctr(25, 1000));
        Assert.Equal(10m, CalculosMarketing.EngajamentoPorSeguidores(100, 1000));
        Assert.Equal(20m, CalculosMarketing.EngajamentoPorAlcance(100, 500));
        Assert.Equal(5m, CalculosMarketing.CrescimentoSeguidores(1050, 1000));
    }
}

public class PeriodoTests
{
    private static readonly DateOnly Hoje = new(2026, 10, 30);

    [Fact]
    public void Ultimos_7_dias_e_periodo_anterior_equivalente()
    {
        var p = PeriodoMarketing.Criar("7d", null, null, Hoje);
        Assert.Equal(new DateOnly(2026, 10, 24), p.Inicio);
        Assert.Equal(Hoje, p.Fim);
        var a = p.Anterior();
        Assert.Equal(new DateOnly(2026, 10, 17), a.Inicio);
        Assert.Equal(new DateOnly(2026, 10, 23), a.Fim);
    }

    [Fact]
    public void Mes_anterior_compara_com_o_mes_antes_dele()
    {
        var p = PeriodoMarketing.Criar("mes_anterior", null, null, Hoje);
        Assert.Equal(new DateOnly(2026, 9, 1), p.Inicio);
        Assert.Equal(new DateOnly(2026, 9, 30), p.Fim);
        var a = p.Anterior();
        Assert.Equal(new DateOnly(2026, 8, 1), a.Inicio);
        Assert.Equal(new DateOnly(2026, 8, 31), a.Fim); // mês civil inteiro
    }

    [Fact]
    public void Personalizado_inverte_datas_e_limita_ao_dia_atual()
    {
        var p = PeriodoMarketing.Criar("personalizado", new DateOnly(2026, 11, 20), new DateOnly(2026, 10, 1), Hoje);
        Assert.Equal(new DateOnly(2026, 10, 1), p.Inicio);
        Assert.Equal(Hoje, p.Fim);
    }

    [Fact]
    public void Codigo_invalido_vira_30_dias()
    {
        Assert.Equal("30d", PeriodoMarketing.Criar("xpto", null, null, Hoje).Codigo);
    }
}

public class SegurancaTests
{
    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = agora;
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private static readonly IDataProtectionProvider Dp = new EphemeralDataProtectionProvider();

    [Fact]
    public void Token_fica_criptografado_e_volta_igual()
    {
        var p = new ProtetorTokens(Dp);
        const string token = "EAAB-token-super-secreto-123456789";
        var protegido = p.Proteger(token);
        Assert.DoesNotContain("secreto", protegido);
        Assert.Equal(token, p.Revelar(protegido));
    }

    [Fact]
    public void State_valido_e_aceito_uma_vez()
    {
        var svc = new OAuthStateService(Dp);
        var g = svc.Gerar("user-1", 7, PlataformaMarketing.Instagram);
        var e = svc.Validar(g.CookieProtegido, g.Estado.State, "user-1", PlataformaMarketing.Instagram, out _);
        Assert.NotNull(e);
        Assert.Equal(7, e!.ClienteId);
        Assert.False(string.IsNullOrEmpty(g.CodeChallenge));
    }

    [Fact]
    public void State_adulterado_outro_usuario_ou_plataforma_sao_rejeitados()
    {
        var svc = new OAuthStateService(Dp);
        var g = svc.Gerar("user-1", 7, PlataformaMarketing.Facebook);
        Assert.Null(svc.Validar(g.CookieProtegido, "state-falso", "user-1", PlataformaMarketing.Facebook, out _));
        Assert.Null(svc.Validar(g.CookieProtegido, g.Estado.State, "user-2", PlataformaMarketing.Facebook, out _));
        Assert.Null(svc.Validar(g.CookieProtegido, g.Estado.State, "user-1", PlataformaMarketing.TikTok, out _));
        Assert.Null(svc.Validar("cookie-forjado", g.Estado.State, "user-1", PlataformaMarketing.Facebook, out _));
        Assert.Null(svc.Validar(null, g.Estado.State, "user-1", PlataformaMarketing.Facebook, out _));
    }

    [Fact]
    public void State_expira_em_10_minutos()
    {
        var relogio = new RelogioFixo(DateTimeOffset.UtcNow);
        var svc = new OAuthStateService(Dp, relogio);
        var g = svc.Gerar("user-1", 7, PlataformaMarketing.GoogleAnalytics);
        relogio.Agora = relogio.Agora.AddMinutes(11);
        Assert.Null(svc.Validar(g.CookieProtegido, g.Estado.State, "user-1", PlataformaMarketing.GoogleAnalytics, out var motivo));
        Assert.Contains("expirou", motivo);
    }

    [Fact]
    public void Sanitizador_remove_tokens_de_mensagens()
    {
        var limpo = Sanitizador.Limpar("Erro em https://graph.facebook.com/me?access_token=EAABsbCS1iHgBAKZBZCabcdefghijkl&x=1 token ya29.a0AfH6SMBabcdefghijklmnop");
        Assert.DoesNotContain("EAABsbCS1", limpo);
        Assert.DoesNotContain("ya29.a0AfH6", limpo);
        Assert.Contains("access_token=***", limpo);
    }
}

public class MapeamentoErrosTests
{
    private static string MetaErro(int code, int? sub = null) =>
        JsonSerializer.Serialize(new { error = new { message = "x", code, error_subcode = sub } });

    [Theory]
    [InlineData(190, null, TipoErroApi.TokenInvalido)]
    [InlineData(190, 460, TipoErroApi.PermissaoRevogada)]
    [InlineData(190, 458, TipoErroApi.PermissaoRevogada)]
    [InlineData(4, null, TipoErroApi.LimiteTaxa)]
    [InlineData(17, null, TipoErroApi.LimiteTaxa)]
    [InlineData(100, null, TipoErroApi.NaoSuportado)]
    [InlineData(10, null, TipoErroApi.NaoSuportado)]
    [InlineData(2, null, TipoErroApi.Temporario)]
    public void Meta(int code, int? sub, TipoErroApi esperado)
    {
        Assert.Equal(esperado, MetaErros.Mapear(HttpStatusCode.BadRequest, MetaErro(code, sub)).Tipo);
    }

    [Fact]
    public void Google_invalid_grant_e_revogacao_e_quota_e_limite()
    {
        Assert.Equal(TipoErroApi.PermissaoRevogada,
            GoogleProvedorBase.MapearErro(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""").Tipo);
        Assert.Equal(TipoErroApi.LimiteTaxa,
            GoogleProvedorBase.MapearErro(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"message":"quota","status":"RESOURCE_EXHAUSTED"}}""").Tipo);
        Assert.Equal(TipoErroApi.TokenInvalido,
            GoogleProvedorBase.MapearErro(HttpStatusCode.Unauthorized, """{"error":{"code":401,"message":"x","status":"UNAUTHENTICATED"}}""").Tipo);
    }

    [Fact]
    public void TikTok_token_invalido_e_escopo()
    {
        Assert.Equal(TipoErroApi.TokenInvalido,
            TikTokProvedor.MapearErro(HttpStatusCode.Unauthorized, """{"error":{"code":"access_token_invalid","message":"x"}}""").Tipo);
        Assert.Equal(TipoErroApi.NaoSuportado,
            TikTokProvedor.MapearErro(HttpStatusCode.Forbidden, """{"error":{"code":"scope_not_authorized","message":"x"}}""").Tipo);
    }
}

public class LeituraRespostasTests
{
    [Fact]
    public void Insights_da_Meta_atribuem_valor_ao_dia_anterior_ao_end_time()
    {
        using var doc = JsonDocument.Parse("""
            {"data":[{"name":"page_impressions_unique","period":"day","values":[
              {"value":120,"end_time":"2026-10-02T07:00:00+0000"},
              {"value":{"like":3,"love":2},"end_time":"2026-10-03T07:00:00+0000"}]}]}
            """);
        var serie = FacebookProvedor.LerSerieInsights(doc.RootElement).ToList();
        Assert.Equal((new DateOnly(2026, 10, 1), 120m), serie[0]);
        Assert.Equal((new DateOnly(2026, 10, 2), 5m), serie[1]); // reações por tipo são somadas
    }

    [Fact]
    public void Instagram_total_value_e_conta_pessoal()
    {
        using var doc = JsonDocument.Parse("""{"data":[{"name":"total_interactions","total_value":{"value":42}},{"name":"views","total_value":{"value":900}}]}""");
        var dados = new DadosSincronizados();
        InstagramProvedor.LerTotais(doc.RootElement, new DateOnly(2026, 10, 1), dados);
        Assert.Contains(dados.Metricas, m => m.Metrica == Metricas.Interacoes && m.Valor == 42);
        Assert.Contains(dados.Metricas, m => m.Metrica == Metricas.Visualizacoes && m.Valor == 900);

        Assert.Null(InstagramProvedor.AvisoTipoConta("BUSINESS"));
        Assert.Null(InstagramProvedor.AvisoTipoConta("MEDIA_CREATOR"));
        Assert.Contains("profissional", InstagramProvedor.AvisoTipoConta("PERSONAL"));
    }

    [Fact]
    public void GA4_runReport_e_quebras()
    {
        using var diario = JsonDocument.Parse("""
            {"rows":[{"dimensionValues":[{"value":"20261001"}],"metricValues":[{"value":"15"},{"value":"4"}]}]}
            """);
        var dados = new DadosSincronizados();
        GoogleAnalyticsProvedor.LerDiario(diario.RootElement, [("activeUsers", Metricas.UsuariosAtivos), ("newUsers", Metricas.NovosUsuarios)], dados);
        Assert.Contains(dados.Metricas, m => m.Data == new DateOnly(2026, 10, 1) && m.Metrica == Metricas.UsuariosAtivos && m.Valor == 15);

        using var quebra = JsonDocument.Parse("""
            {"rows":[{"dimensionValues":[{"value":"20261001"},{"value":"Organic Search"}],"metricValues":[{"value":"30"}]}]}
            """);
        GoogleAnalyticsProvedor.LerQuebra(quebra.RootElement, Dimensoes.Canal, [("sessions", Metricas.Sessoes)], dados);
        Assert.Contains(dados.Dimensoes, d => d.ValorDimensao == "Organic Search" && d.Valor == 30);
    }

    [Fact]
    public void Search_Console_guarda_posicao_ponderada_por_impressoes()
    {
        using var doc = JsonDocument.Parse("""{"rows":[{"keys":["2026-10-01","agencia de marketing"],"clicks":3,"impressions":100,"ctr":0.03,"position":4.5}]}""");
        var dados = new DadosSincronizados();
        SearchConsoleProvedor.LerQuebra(doc.RootElement, Dimensoes.PalavraChave, dados);
        Assert.Contains(dados.Dimensoes, d => d.Metrica == Metricas.PosicaoPonderada && d.Valor == 450m);
        Assert.Contains(dados.Dimensoes, d => d.Metrica == Metricas.CliquesOrganicos && d.Valor == 3m);
    }
}
