using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Marketing;
using AgenciaOS.Services.Marketing.Provedores;
using AgenciaOS.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>Contas e Integrações: conexão OAuth, seleção de contas, atualização e desconexão.</summary>
[Authorize]
public class IntegracoesController(
    ApplicationDbContext db,
    UserManager<Usuario> userManager,
    AcessoMarketingService acesso,
    IntegracaoMarketingService integracao,
    RegistroProvedores provedores,
    OAuthStateService oauthState,
    MarketingOptions opt,
    ILogger<IntegracoesController> logger) : MarketingControllerBase(userManager, acesso)
{
    public async Task<IActionResult> Index(int? clienteId)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true }) return Forbid();

        var clientes = await Acesso.Clientes(ctx).OrderBy(c => c.NomeEmpresa).ToListAsync();
        if (!ctx.EhAgencia) clienteId = ctx.ClienteProprioId;
        else if (clienteId != null && clientes.All(c => c.Id != clienteId)) return Forbid();

        var cliente = clientes.FirstOrDefault(c => c.Id == clienteId);
        var cards = new List<CardIntegracao>();
        if (cliente != null)
        {
            var conexoes = await db.ConexoesMarketing.Where(c => c.ClienteId == cliente.Id).ToListAsync();
            var contas = await db.ContasMarketing.Where(c => c.ClienteId == cliente.Id)
                .OrderBy(c => c.Nome)
                .Select(c => new ContaResumo
                {
                    Id = c.Id, ClienteId = c.ClienteId, Plataforma = c.Plataforma, Nome = c.Nome, NomeUsuario = c.NomeUsuario,
                    FotoUrl = c.FotoUrl, Status = c.Status, Ativa = c.Ativa, UltimaSincronizacao = c.UltimaSincronizacao,
                    UltimoErro = c.UltimoErro, Limitacao = c.Limitacao,
                    Indisponiveis = c.MetricasIndisponiveis == null ? Array.Empty<string>() : c.MetricasIndisponiveis.Split(',', StringSplitOptions.None)
                }).ToListAsync();

            foreach (var p in Enum.GetValues<PlataformaMarketing>())
            {
                var conexao = conexoes.FirstOrDefault(c => c.Plataforma == p);
                var contasP = contas.Where(c => c.Plataforma == p).ToList();
                cards.Add(new CardIntegracao
                {
                    Plataforma = p,
                    Conexao = conexao,
                    Contas = contasP,
                    VariaveisPendentes = opt.VariaveisPendentes(p),
                    Status = StatusCard(conexao, contasP),
                    Erro = conexao?.UltimoErro ?? contasP.Where(c => c.Ativa).Select(c => c.UltimoErro).FirstOrDefault(e => e != null)
                });
            }
        }

        return View(new IntegracoesViewModel
        {
            Cliente = cliente, EhAgencia = ctx.EhAgencia, Clientes = clientes, Cards = cards, RedirectBase = BaseUrl()
        });
    }

    internal static StatusIntegracao StatusCard(ConexaoMarketing? conexao, List<ContaResumo> contas)
    {
        if (conexao == null || conexao.Status == StatusIntegracao.NaoConectado) return StatusIntegracao.NaoConectado;
        if (conexao.Status is StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada) return conexao.Status;
        var ativas = contas.Where(c => c.Ativa).ToList();
        if (ativas.Count == 0) return StatusIntegracao.Conectando; // autorizou mas ainda não escolheu a conta
        if (ativas.Any(c => c.Status == StatusIntegracao.Sincronizando)) return StatusIntegracao.Sincronizando;
        if (ativas.Any(c => c.Status is StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada or StatusIntegracao.ErroSincronizacao))
            return ativas.First(c => c.Status is StatusIntegracao.TokenExpirado or StatusIntegracao.PermissaoRevogada or StatusIntegracao.ErroSincronizacao).Status;
        return StatusIntegracao.Conectado;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(MarketingServiceCollectionExtensions.PoliticaRateLimit)]
    public async Task<IActionResult> Conectar(PlataformaMarketing plataforma, int clienteId)
    {
        var ctx = await ContextoAsync();
        if (ctx == null || !ctx.PodeAcessarCliente(clienteId) || !Enum.IsDefined(plataforma)) return Forbid();
        if (!await Acesso.Clientes(ctx).AnyAsync(c => c.Id == clienteId)) return Forbid();

        var pendentes = opt.VariaveisPendentes(plataforma);
        if (pendentes.Length > 0)
        {
            TempData["Erro"] = $"{PlataformaInfo.Nome(plataforma)} ainda não está configurado no servidor. Variáveis pendentes: {string.Join(", ", pendentes)}.";
            return RedirectToAction(nameof(Index), new { clienteId });
        }

        var gerado = oauthState.Gerar(ctx.Usuario.Id, clienteId, plataforma);
        Response.Cookies.Append(OAuthStateService.NomeCookie, gerado.CookieProtegido, OAuthStateService.OpcoesCookie(Request.IsHttps));

        var url = provedores.Obter(plataforma).MontarUrlAutorizacao(gerado.Estado.State, RedirectUri(plataforma), gerado.CodeChallenge);
        return Redirect(url);
    }

    [HttpGet("/Integracoes/Callback/{plataforma}")]
    [EnableRateLimiting(MarketingServiceCollectionExtensions.PoliticaRateLimit)]
    public async Task<IActionResult> Callback(string plataforma, string? code, string? state, string? error, string? error_description, CancellationToken ct)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true } || !PlataformaInfo.TentarSlug(plataforma, out var p)) return Forbid();

        var cookie = Request.Cookies[OAuthStateService.NomeCookie];
        Response.Cookies.Delete(OAuthStateService.NomeCookie, new CookieOptions { Path = "/Integracoes" });

        var estado = oauthState.Validar(cookie, state, ctx.Usuario.Id, p, out var motivo);
        if (estado == null || !ctx.PodeAcessarCliente(estado.ClienteId))
        {
            logger.LogWarning("Callback OAuth rejeitado para {Plataforma}: {Motivo}", p, motivo);
            TempData["Erro"] = string.IsNullOrEmpty(motivo) ? "Autorização inválida." : motivo;
            return RedirectToAction(nameof(Index));
        }

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            TempData["Erro"] = error is "access_denied" or "user_denied"
                ? $"A autorização do {PlataformaInfo.Nome(p)} foi cancelada ou negada."
                : $"O {PlataformaInfo.Nome(p)} retornou um erro: {Sanitizador.Limpar(error_description ?? error ?? "código ausente")}";
            return RedirectToAction(nameof(Index), new { clienteId = estado.ClienteId });
        }

        try
        {
            var token = await provedores.Obter(p).TrocarCodigoAsync(code, RedirectUri(p), estado.CodeVerifier, ct);
            var conexao = await integracao.SalvarConexaoAsync(estado.ClienteId, p, token, ctx.Usuario.Id, ct);
            return RedirectToAction(nameof(Selecionar), new { conexaoId = conexao.Id });
        }
        catch (ErroApiExterna e)
        {
            TempData["Erro"] = $"Não foi possível concluir a conexão com o {PlataformaInfo.Nome(p)}: {e.Message}";
            return RedirectToAction(nameof(Index), new { clienteId = estado.ClienteId });
        }
    }

    public async Task<IActionResult> Selecionar(int conexaoId, CancellationToken ct)
    {
        var (ctx, conexao) = await ConexaoPermitidaAsync(conexaoId);
        if (conexao == null) return Forbid();

        var cliente = await db.Clientes.FirstAsync(c => c.Id == conexao.ClienteId, ct);
        var vinculadas = await db.ContasMarketing.Where(c => c.ConexaoMarketingId == conexao.Id && c.Ativa)
            .Select(c => c.IdExterno).ToListAsync(ct);
        try
        {
            var disponiveis = await integracao.ListarDisponiveisAsync(conexao, ct);
            return View(new SelecionarContasViewModel
            {
                ConexaoId = conexao.Id, Cliente = cliente, Plataforma = conexao.Plataforma, Disponiveis = disponiveis, JaVinculadas = [.. vinculadas],
                Erro = disponiveis.Count == 0 ? MensagemSemContas(conexao.Plataforma) : null
            });
        }
        catch (ErroApiExterna e)
        {
            return View(new SelecionarContasViewModel { ConexaoId = conexao.Id, Cliente = cliente, Plataforma = conexao.Plataforma, Erro = e.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Selecionar(int conexaoId, string[] contas, CancellationToken ct)
    {
        var (_, conexao) = await ConexaoPermitidaAsync(conexaoId);
        if (conexao == null) return Forbid();
        if (contas.Length == 0)
        {
            TempData["Erro"] = "Selecione pelo menos uma conta para vincular.";
            return RedirectToAction(nameof(Selecionar), new { conexaoId });
        }
        try
        {
            var n = await integracao.VincularAsync(conexao, contas, ct);
            TempData[n > 0 ? "Sucesso" : "Erro"] = n > 0
                ? $"{n} conta(s) vinculada(s). A primeira sincronização foi iniciada e os dados aparecem em alguns minutos."
                : "Nenhuma das contas selecionadas está autorizada para este acesso.";
        }
        catch (ErroApiExterna e)
        {
            TempData["Erro"] = $"Não foi possível vincular: {e.Message}";
        }
        return RedirectToAction(nameof(Index), new { clienteId = conexao.ClienteId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(MarketingServiceCollectionExtensions.PoliticaRateLimit)]
    public async Task<IActionResult> Atualizar(int contaId, CancellationToken ct)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true }) return Forbid();
        var conta = await Acesso.Contas(ctx).FirstOrDefaultAsync(c => c.Id == contaId, ct);
        if (conta == null) return Forbid();

        var erro = await integracao.SolicitarAtualizacaoAsync(conta.Id, ct);
        TempData[erro == null ? "Sucesso" : "Erro"] = erro ?? "Atualização solicitada. Os dados serão sincronizados em instantes.";
        return RedirectToAction(nameof(Index), new { clienteId = conta.ClienteId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Desconectar(int conexaoId, CancellationToken ct)
    {
        var (_, conexao) = await ConexaoPermitidaAsync(conexaoId);
        if (conexao == null) return Forbid();
        TempData["Sucesso"] = await integracao.DesconectarAsync(conexao, ct);
        return RedirectToAction(nameof(Index), new { clienteId = conexao.ClienteId });
    }

    private async Task<(AcessoMarketingService.Contexto?, ConexaoMarketing?)> ConexaoPermitidaAsync(int conexaoId)
    {
        var ctx = await ContextoAsync();
        if (ctx is not { PodeAcessarModulo: true }) return (ctx, null);
        var conexao = await db.ConexoesMarketing.FirstOrDefaultAsync(c => c.Id == conexaoId);
        return conexao != null && ctx.PodeAcessarCliente(conexao.ClienteId) ? (ctx, conexao) : (ctx, null);
    }

    private static string MensagemSemContas(PlataformaMarketing p) => p switch
    {
        PlataformaMarketing.Facebook => "Nenhuma página foi encontrada. Verifique se você administra alguma página e se ela foi marcada na tela de autorização do Facebook.",
        PlataformaMarketing.GoogleAnalytics => "Nenhuma propriedade do Google Analytics 4 foi encontrada para esta conta Google.",
        PlataformaMarketing.SearchConsole => "Nenhuma propriedade verificada do Search Console foi encontrada para esta conta Google.",
        _ => "Nenhuma conta disponível foi retornada pela plataforma."
    };

    private string BaseUrl() => (opt.AppBaseUrl ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
    private string RedirectUri(PlataformaMarketing p) => $"{BaseUrl()}/Integracoes/Callback/{PlataformaInfo.Slug(p)}";
}

/// <summary>Base dos controllers do módulo: resolve o contexto de acesso (isolamento por cliente).</summary>
public abstract class MarketingControllerBase(UserManager<Usuario> userManager, AcessoMarketingService acesso) : Controller
{
    protected AcessoMarketingService Acesso => acesso;

    protected async Task<AcessoMarketingService.Contexto?> ContextoAsync()
    {
        var usuario = await userManager.GetUserAsync(User);
        if (usuario == null) return null;
        return await acesso.ObterAsync(usuario, User.IsInRole("Admin"), User.IsInRole("Equipe"));
    }
}
