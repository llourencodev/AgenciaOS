using AgenciaOS.Data;
using AgenciaOS.Models;
using AgenciaOS.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgenciaOS.Controllers;

[Authorize]
public class DashboardController(ApplicationDbContext db, UserManager<Usuario> userManager) : Controller
{
    public async Task<IActionResult> Index()
    {
        var usuario = await userManager.GetUserAsync(User);
        if (usuario == null) return RedirectToAction("Login", "Account");

        var vm = new DashboardViewModel();

        if (usuario.Tipo == TipoUsuario.Cliente)
        {
            var cliente = await db.Clientes.FirstOrDefaultAsync(c => c.UsuarioClienteId == usuario.Id);
            if (cliente != null)
            {
                vm.ClienteId = cliente.Id;
                vm.NomeCliente = cliente.NomeEmpresa;
                vm.ConteudosMes = await db.Conteudos.CountAsync(c => c.ClienteId == cliente.Id && c.DataPrevista.Month == DateTime.Now.Month);
                vm.AprovacoesPendentes = await db.Conteudos.CountAsync(c => c.ClienteId == cliente.Id && c.Status == StatusConteudo.AguardandoAprovacao);
                vm.ConteudosAprovados = await db.Conteudos.CountAsync(c => c.ClienteId == cliente.Id && c.Status == StatusConteudo.Aprovado);
                vm.ConteudosPublicados = await db.Conteudos.CountAsync(c => c.ClienteId == cliente.Id && c.Status == StatusConteudo.Publicado);
                vm.UltimosConteudos = await db.Conteudos.Where(c => c.ClienteId == cliente.Id)
                    .OrderByDescending(c => c.CriadoEm).Take(5).ToListAsync();
            }
            return View("DashboardCliente", vm);
        }

        vm.TotalClientes = await db.Clientes.CountAsync(c => c.Ativo);
        vm.TarefasPendentes = await db.Tarefas.CountAsync(t => t.Status != StatusTarefa.Concluido);
        vm.TarefasAtrasadas = await db.Tarefas.CountAsync(t => t.Status != StatusTarefa.Concluido && t.Prazo < DateTime.UtcNow);
        vm.AprovacoesPendentes = await db.Conteudos.CountAsync(c => c.Status == StatusConteudo.AguardandoAprovacao);
        vm.ConteudosAtrasados = await db.Conteudos.CountAsync(c => c.DataPrevista < DateTime.UtcNow && c.Status != StatusConteudo.Publicado);

        vm.UltimasTarefas = await db.Tarefas.Include(t => t.Cliente).Include(t => t.Responsavel)
            .Where(t => t.Status != StatusTarefa.Concluido)
            .OrderBy(t => t.Prazo).Take(5).ToListAsync();

        vm.UltimosConteudos = await db.Conteudos.Include(c => c.Cliente)
            .Where(c => c.Status == StatusConteudo.AguardandoAprovacao)
            .OrderByDescending(c => c.CriadoEm).Take(5).ToListAsync();

        var hoje = DateTime.Now;
        vm.ReceitaMes = await db.Financeiros
            .Where(f => f.Tipo == TipoFinanceiro.Entrada && f.DataVencimento.Month == hoje.Month && f.DataVencimento.Year == hoje.Year && f.Pago)
            .SumAsync(f => (decimal?)f.Valor) ?? 0;

        vm.DespesaMes = await db.Financeiros
            .Where(f => f.Tipo == TipoFinanceiro.Saida && f.DataVencimento.Month == hoje.Month && f.DataVencimento.Year == hoje.Year && f.Pago)
            .SumAsync(f => (decimal?)f.Valor) ?? 0;

        return View(vm);
    }
}
