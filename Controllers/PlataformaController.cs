using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.Services.Cobranca;
using AgenciaOS.Services.Plataforma;
using AgenciaOS.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

/// <summary>
/// Painel exclusivo do proprietário da plataforma, separado da navegação das agências.
/// Toda ação que altera dados gera registro de auditoria.
/// </summary>
[Authorize(Roles = "SuperAdmin")]
public class PlataformaController(ApplicationDbContext db, UserManager<Usuario> userManager, TenantContext tenant,
    MetricasPlataformaService metricas, AuditoriaService auditoria, ConfiguracaoPlataformaService configuracao,
    PrivacidadeService privacidade, CobrancaService cobranca,
    Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService saude) : Controller
{
    /// <summary>Somente SuperAdmin chega aqui ([Authorize]); o painel lê e administra todas as agências.</summary>
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context)
    {
        tenant.UsarSistema();
        base.OnActionExecuting(context);
    }

    // ── Detalhes e administração de uma agência ──────────────────────────
    public record DetalhesAgencia(Agencia Agencia, List<Usuario> Usuarios, Assinatura? Assinatura, List<Pagamento> Pagamentos,
        List<RegistroAuditoria> Atividades, int Clientes, List<Plano> Planos);

    public async Task<IActionResult> Agencia(int id, CancellationToken ct)
    {
        var ag = await db.Agencias.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ag == null) return NotFound();
        return View(new DetalhesAgencia(ag,
            await db.Users.Where(u => u.AgenciaId == id).OrderBy(u => u.Nome).ToListAsync(ct),
            await cobranca.VigenteAsync(id, ct),
            await db.Pagamentos.Where(p => p.AgenciaId == id).OrderByDescending(p => p.Vencimento).Take(24).ToListAsync(ct),
            await db.RegistrosAuditoria.Where(r => r.AgenciaId == id).OrderByDescending(r => r.Quando).Take(50).ToListAsync(ct),
            await db.Clientes.CountAsync(c => c.AgenciaId == id, ct),
            await db.Planos.OrderBy(p => p.Ordem).ToListAsync(ct)));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Suspender(int id, string motivo, CancellationToken ct)
    {
        var ag = await db.Agencias.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ag == null) return NotFound();
        if (string.IsNullOrWhiteSpace(motivo)) { TempData["Erro"] = "Informe o motivo da suspensão."; return RedirectToAction(nameof(Agencia), new { id }); }
        ag.Status = StatusAgencia.Suspensa;
        ag.MotivoSuspensao = motivo.Trim();
        ag.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        // Derruba as sessões abertas da agência
        foreach (var u in await db.Users.Where(u => u.AgenciaId == id).ToListAsync(ct))
            await userManager.UpdateSecurityStampAsync(u);
        await auditoria.RegistrarAsync("plataforma.suspender-agencia", id, "Agencia", id.ToString(), new { motivo }, ct);
        TempData["Sucesso"] = "Agência suspensa. Os dados foram preservados.";
        return RedirectToAction(nameof(Agencia), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reativar(int id, CancellationToken ct)
    {
        var ag = await db.Agencias.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ag == null) return NotFound();
        ag.Status = StatusAgencia.Ativa;
        ag.MotivoSuspensao = null;
        ag.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("plataforma.reativar-agencia", id, "Agencia", id.ToString(), ct: ct);
        TempData["Sucesso"] = "Agência reativada.";
        return RedirectToAction(nameof(Agencia), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Isencao(int id, bool isenta, CancellationToken ct)
    {
        var ag = await db.Agencias.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ag == null) return NotFound();
        ag.Isenta = isenta;
        if (isenta && ag.SituacaoAssinatura != SituacaoAssinatura.Ativa) ag.SituacaoAssinatura = SituacaoAssinatura.Ativa;
        ag.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync(isenta ? "plataforma.isentar-agencia" : "plataforma.remover-isencao", id, "Agencia", id.ToString(), ct: ct);
        TempData["Sucesso"] = isenta ? "Agência isenta de assinatura." : "Isenção removida: a agência passa a seguir as regras de assinatura.";
        return RedirectToAction(nameof(Agencia), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlterarPlanoAgencia(int id, int planoId, CancellationToken ct)
    {
        var ag = await db.Agencias.FirstOrDefaultAsync(a => a.Id == id, ct);
        var plano = await db.Planos.FirstOrDefaultAsync(p => p.Id == planoId, ct);
        if (ag == null || plano == null) return NotFound();
        try
        {
            if (await cobranca.VigenteAsync(id, ct) is { CanceladaEm: null })
                await cobranca.AlterarPlanoAsync(id, planoId, ct); // assinatura paga: atualiza o valor no gateway
            else
            {
                ag.PlanoCodigo = plano.Codigo; // isenta/teste: só define limites e módulos
                ag.AtualizadoEm = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            await auditoria.RegistrarAsync("plataforma.alterar-plano", id, "Agencia", id.ToString(), new { plano = plano.Codigo }, ct);
            TempData["Sucesso"] = $"Plano alterado para {plano.Nome}.";
        }
        catch (Services.Cobranca.ErroGateway e) { TempData["Erro"] = e.Message; }
        return RedirectToAction(nameof(Agencia), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EstenderTeste(int id, int dias, CancellationToken ct)
    {
        var ag = await db.Agencias.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (ag == null || dias is < 1 or > 90) return NotFound();
        var base_ = ag.TesteExpiraEm is { } fim && fim > DateTime.UtcNow ? fim : DateTime.UtcNow;
        ag.TesteExpiraEm = base_.AddDays(dias);
        if (ag.SituacaoAssinatura == SituacaoAssinatura.Suspensa && !await db.Assinaturas.AnyAsync(a => a.AgenciaId == id && a.Vigente, ct))
            ag.SituacaoAssinatura = SituacaoAssinatura.Teste;
        ag.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await auditoria.RegistrarAsync("plataforma.estender-teste", id, "Agencia", id.ToString(), new { dias, ate = ag.TesteExpiraEm }, ct);
        TempData["Sucesso"] = $"Teste estendido até {ag.TesteExpiraEm:dd/MM/yyyy}.";
        return RedirectToAction(nameof(Agencia), new { id });
    }

    // ── Saúde da operação (detalhes que a página pública não mostra) ─────
    public record PainelSaude(Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport Relatorio, int ContasMarketingComErro,
        int WebhooksComFalha, int PedidosPrivacidadeAtrasados);

    public async Task<IActionResult> Saude(CancellationToken ct) => View(new PainelSaude(
        await saude.CheckHealthAsync(ct),
        await db.ContasMarketing.CountAsync(c => c.Ativa && (c.Status == StatusIntegracao.ErroSincronizacao
            || c.Status == StatusIntegracao.TokenExpirado || c.Status == StatusIntegracao.PermissaoRevogada), ct),
        await db.EventosWebhook.CountAsync(e => e.ProcessadoEm == null && e.Erro != null, ct),
        await db.SolicitacoesPrivacidade.CountAsync(s => s.Status != StatusSolicitacaoPrivacidade.Concluida
            && s.Status != StatusSolicitacaoPrivacidade.Recusada && s.PrazoEm < DateTime.UtcNow, ct)));

    // ── Domínios das agências (para configurar o domínio/HTTPS no Railway) ──
    public async Task<IActionResult> Dominios(CancellationToken ct) =>
        View((await db.DominiosAgencia.Join(db.Agencias, d => d.AgenciaId, a => a.Id, (d, a) => new { d, a.Nome })
            .OrderBy(x => x.d.Status).ThenBy(x => x.d.Host).ToListAsync(ct))
            .Select(x => new LinhaDominio(x.d, x.Nome)).ToList());

    public record LinhaDominio(DominioAgencia Dominio, string Agencia);

    // ── Auditoria ────────────────────────────────────────────────────────
    public async Task<IActionResult> Auditoria(int? agenciaId, string? acao, CancellationToken ct)
    {
        var q = db.RegistrosAuditoria.AsQueryable();
        if (agenciaId.HasValue) q = q.Where(r => r.AgenciaId == agenciaId);
        if (!string.IsNullOrWhiteSpace(acao)) q = q.Where(r => r.Acao.Contains(acao));
        ViewData["AgenciaId"] = agenciaId;
        ViewData["Acao"] = acao;
        return View(await q.OrderByDescending(r => r.Quando).Take(300).ToListAsync(ct));
    }

    // ── Configurações globais ────────────────────────────────────────────
    public async Task<IActionResult> Configuracoes() => View(await configuracao.ObterAsync());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Configuracoes(ConfiguracaoPlataforma input, CancellationToken ct)
    {
        await configuracao.SalvarAsync(input, ct);
        await auditoria.RegistrarAsync("plataforma.configuracoes", null, "ConfiguracaoPlataforma", "1",
            new { input.DiasTeste, input.DiasTolerancia, input.ExigirAceiteTermos }, ct);
        TempData["Sucesso"] = "Configurações salvas.";
        return RedirectToAction(nameof(Configuracoes));
    }

    // ── Solicitações de titulares (LGPD) ─────────────────────────────────
    public async Task<IActionResult> Privacidade(CancellationToken ct) =>
        View(await db.SolicitacoesPrivacidade
            .OrderBy(s => s.Status == StatusSolicitacaoPrivacidade.Concluida || s.Status == StatusSolicitacaoPrivacidade.Recusada)
            .ThenBy(s => s.PrazoEm).Take(300).ToListAsync(ct));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResponderPrivacidade(int id, StatusSolicitacaoPrivacidade status, string? resposta, int? agenciaId, CancellationToken ct)
    {
        var s = await db.SolicitacoesPrivacidade.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s == null) return NotFound();
        if (status == StatusSolicitacaoPrivacidade.EncaminhadaAgencia)
        {
            if (agenciaId is not int ag || !await db.Agencias.AnyAsync(a => a.Id == ag, ct))
            {
                TempData["Erro"] = "Escolha a agência para encaminhar.";
                return RedirectToAction(nameof(Privacidade));
            }
            s.AgenciaId = ag;
        }
        await privacidade.ResponderAsync(s, status, resposta, userManager.GetUserId(User)!, ct);
        await auditoria.RegistrarAsync("privacidade.responder", s.AgenciaId, "SolicitacaoPrivacidade", s.Protocolo, new { status = status.ToString() }, ct);
        TempData["Sucesso"] = $"Solicitação {s.Protocolo} atualizada.";
        return RedirectToAction(nameof(Privacidade));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportarDadosTitular(int id, CancellationToken ct)
    {
        var s = await db.SolicitacoesPrivacidade.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s?.UsuarioId == null) return NotFound();
        var bytes = await privacidade.ExportarUsuarioAsync(s.UsuarioId, ct);
        await auditoria.RegistrarAsync("privacidade.exportar-titular", s.AgenciaId, "SolicitacaoPrivacidade", s.Protocolo, ct: ct);
        return File(bytes, "application/json", $"dados-{s.Protocolo}.json");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AnonimizarTitular(int id, string confirmacao, CancellationToken ct)
    {
        var s = await db.SolicitacoesPrivacidade.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s?.UsuarioId == null) return NotFound();
        if (confirmacao != "EXCLUIR") { TempData["Erro"] = "Digite EXCLUIR para confirmar."; return RedirectToAction(nameof(Privacidade)); }
        try
        {
            await privacidade.AnonimizarUsuarioAsync(s.UsuarioId, ct);
            await auditoria.RegistrarAsync("privacidade.anonimizar-titular", s.AgenciaId, "SolicitacaoPrivacidade", s.Protocolo, ct: ct);
            TempData["Sucesso"] = "Dados do titular anonimizados e acesso encerrado. Registros de aceite foram mantidos (obrigação legal).";
        }
        catch (InvalidOperationException e) { TempData["Erro"] = e.Message; }
        return RedirectToAction(nameof(Privacidade));
    }
    public class NovaAgenciaInput
    {
        [Required, StringLength(150)] public string Nome { get; set; } = "";
        [StringLength(150)] public string? NomeFantasia { get; set; }
        [StringLength(60), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Use letras minúsculas, números e hífen.")]
        public string? Slug { get; set; }
        [StringLength(18)] public string? Cnpj { get; set; }
        [EmailAddress, StringLength(256)] public string? EmailPrincipal { get; set; }
        [StringLength(30)] public string? Telefone { get; set; }
        [Required, StringLength(100)] public string AdminNome { get; set; } = "";
        [Required, EmailAddress] public string AdminEmail { get; set; } = "";
        [Required, MinLength(10)] public string AdminSenha { get; set; } = "";
    }

    public record LinhaAgencia(Agencia Agencia, int Usuarios, int Clientes);

    public async Task<IActionResult> Index(string? busca)
    {
        ViewData["Indicadores"] = await metricas.CalcularAsync(DateTime.UtcNow);
        ViewData["PrivacidadePendentes"] = await db.SolicitacoesPrivacidade.CountAsync(s =>
            s.Status != StatusSolicitacaoPrivacidade.Concluida && s.Status != StatusSolicitacaoPrivacidade.Recusada);

        var query = db.Agencias.AsQueryable();
        if (!string.IsNullOrWhiteSpace(busca))
            query = query.Where(a => a.Nome.Contains(busca) || (a.NomeFantasia != null && a.NomeFantasia.Contains(busca)) || a.Slug.Contains(busca));

        var agencias = await query.OrderBy(a => a.Nome).ToListAsync();
        var usuarios = await db.Users.Where(u => u.AgenciaId != null).GroupBy(u => u.AgenciaId!.Value)
            .Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total);
        var clientes = await db.Clientes.GroupBy(c => c.AgenciaId)
            .Select(g => new { g.Key, Total = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Total);

        ViewData["Busca"] = busca;
        return View(agencias.Select(a => new LinhaAgencia(a, usuarios.GetValueOrDefault(a.Id), clientes.GetValueOrDefault(a.Id))).ToList());
    }

    public IActionResult NovaAgencia() => View(new NovaAgenciaInput());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> NovaAgencia(NovaAgenciaInput input)
    {
        input.Slug = string.IsNullOrWhiteSpace(input.Slug) ? GerarSlug(input.NomeFantasia ?? input.Nome) : input.Slug.Trim();
        if (await db.Agencias.AnyAsync(a => a.Slug == input.Slug))
            ModelState.AddModelError(nameof(input.Slug), "Já existe uma agência com esse identificador.");
        if (await userManager.FindByEmailAsync(input.AdminEmail) != null)
            ModelState.AddModelError(nameof(input.AdminEmail), "Este e-mail já é usado por outro usuário da plataforma.");
        if (!ModelState.IsValid) return View(input);

        tenant.UsarSistema();
        await using var tx = await db.Database.BeginTransactionAsync();

        var agencia = new Agencia
        {
            Nome = input.Nome.Trim(), NomeFantasia = input.NomeFantasia?.Trim(), Slug = input.Slug,
            Cnpj = input.Cnpj, EmailPrincipal = input.EmailPrincipal, Telefone = input.Telefone,
            Status = StatusAgencia.Ativa, SituacaoAssinatura = SituacaoAssinatura.Teste
        };
        db.Agencias.Add(agencia);
        await db.SaveChangesAsync();

        db.Configuracoes.Add(new Configuracao { AgenciaId = agencia.Id, NomeAgencia = agencia.NomeExibicao });
        await db.SaveChangesAsync();

        var admin = new Usuario
        {
            UserName = input.AdminEmail, Email = input.AdminEmail, Nome = input.AdminNome.Trim(),
            Tipo = TipoUsuario.Admin, EmailConfirmed = true, AgenciaId = agencia.Id
        };
        var r = await userManager.CreateAsync(admin, input.AdminSenha);
        if (!r.Succeeded)
        {
            await tx.RollbackAsync();
            foreach (var e in r.Errors) ModelState.AddModelError(nameof(input.AdminSenha), e.Description);
            return View(input);
        }
        await userManager.AddToRoleAsync(admin, "Admin");
        await tx.CommitAsync();

        await auditoria.RegistrarAsync("plataforma.criar-agencia", agencia.Id, "Agencia", agencia.Id.ToString(), new { agencia.Slug, admin = admin.Email });
        TempData["Sucesso"] = $"Agência {agencia.NomeExibicao} criada. Administrador: {admin.Email}.";
        return RedirectToAction(nameof(Index));
    }

    // ── Planos (cadastrados aqui, sem alterar código) ────────────────────
    public async Task<IActionResult> Planos() => View(await db.Planos.OrderBy(p => p.Ordem).ThenBy(p => p.Preco).ToListAsync());

    public async Task<IActionResult> Plano(int? id)
    {
        if (id == null) return View(new Plano { Ativo = true });
        var plano = await db.Planos.FindAsync(id);
        return plano == null ? NotFound() : View(plano);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Plano(Plano input, string[]? modulos)
    {
        input.Codigo = GerarSlug(string.IsNullOrWhiteSpace(input.Codigo) ? input.Nome : input.Codigo);
        if (string.IsNullOrWhiteSpace(input.Nome)) ModelState.AddModelError(nameof(input.Nome), "Informe o nome.");
        if (input.Preco < 0) ModelState.AddModelError(nameof(input.Preco), "Preço inválido.");
        if (await db.Planos.AnyAsync(p => p.Codigo == input.Codigo && p.Id != input.Id))
            ModelState.AddModelError(nameof(input.Codigo), "Já existe um plano com esse código.");
        if (!ModelState.IsValid) return View(input);

        var plano = input.Id == 0 ? new Plano() : await db.Planos.FindAsync(input.Id);
        if (plano == null) return NotFound();
        if (input.Id != 0 && plano.Codigo != input.Codigo && await db.Assinaturas.IgnoreQueryFilters().AnyAsync(a => a.PlanoId == plano.Id))
        {
            ModelState.AddModelError(nameof(input.Codigo), "O código não pode mudar depois que o plano tem assinaturas.");
            return View(input);
        }

        plano.Codigo = input.Codigo;
        plano.Nome = input.Nome.Trim();
        plano.Descricao = input.Descricao;
        // Preço novo vale para novas contratações; assinaturas existentes mantêm o valor contratado
        plano.Preco = input.Preco;
        plano.Periodicidade = input.Periodicidade;
        plano.LimiteUsuarios = input.LimiteUsuarios;
        plano.LimiteClientes = input.LimiteClientes;
        plano.LimiteArmazenamentoMb = input.LimiteArmazenamentoMb;
        plano.Modulos = modulos is { Length: > 0 } ? string.Join(",", modulos.Where(m => ModulosDisponiveis.Contains(m))) : null;
        plano.RecursosAdicionais = input.RecursosAdicionais;
        plano.Ativo = input.Ativo;
        plano.Ordem = input.Ordem;
        plano.AtualizadoEm = DateTime.UtcNow;
        if (plano.Id == 0) db.Planos.Add(plano);
        await db.SaveChangesAsync();
        await auditoria.RegistrarAsync("plataforma.salvar-plano", null, "Plano", plano.Codigo, new { plano.Preco, plano.Ativo, plano.LimiteUsuarios, plano.LimiteClientes, plano.LimiteArmazenamentoMb, plano.Modulos });
        TempData["Sucesso"] = $"Plano {plano.Nome} salvo.";
        return RedirectToAction(nameof(Planos));
    }

    public static readonly string[] ModulosDisponiveis = ["Clientes", "Calendario", "Arquivos", "Tarefas", "Financeiro", "Contratos", "DatasComemorativas", "Marketing"];

    internal static string GerarSlug(string texto)
    {
        var normalizado = new string(texto.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        var slug = Regex.Replace(normalizado, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
