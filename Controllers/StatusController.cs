using AgenciaOS.Services.Operacao;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgenciaOS.Controllers;

/// <summary>
/// Página pública de status. Mostra só nome e situação de cada componente:
/// mensagens de erro, tempos e detalhes ficam no painel global (/Plataforma/Saude).
/// </summary>
[AllowAnonymous]
public class StatusController(HealthCheckService saude) : Controller
{
    public record Componente(string Nome, string Situacao);
    public record Painel(string Geral, DateTime VerificadoEm, List<Componente> Componentes);

    private static readonly Dictionary<string, string> Nomes = new()
    {
        ["banco"] = "Banco de dados",
        ["armazenamento"] = "Arquivos",
        ["tarefas"] = "Rotinas automáticas (sincronização, assinaturas)",
        ["integracoes"] = "E-mail e pagamentos",
    };

    private static string Traduzir(HealthStatus s) => s switch
    {
        HealthStatus.Healthy => "Operacional",
        HealthStatus.Degraded => "Instabilidade",
        _ => "Fora do ar"
    };

    private async Task<Painel> MontarAsync(CancellationToken ct)
    {
        var r = await saude.CheckHealthAsync(c => c.Tags.Contains(TagsSaude.Status), ct);
        var componentes = new List<Componente> { new("Aplicação", "Operacional") };
        componentes.AddRange(r.Entries.Select(e => new Componente(Nomes.GetValueOrDefault(e.Key, e.Key), Traduzir(e.Value.Status))));
        return new Painel(Traduzir(r.Status), DateTime.UtcNow, componentes);
    }

    [HttpGet("/status")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await MontarAsync(ct));

    [HttpGet("/status.json")]
    [ResponseCache(Duration = 30, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> Json(CancellationToken ct) => Ok(await MontarAsync(ct));
}
