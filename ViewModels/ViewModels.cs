using System.ComponentModel.DataAnnotations;
using AgenciaOS.Models;

namespace AgenciaOS.ViewModels;

public class LoginViewModel
{
    [Required(ErrorMessage = "E-mail obrigatório")]
    [EmailAddress(ErrorMessage = "E-mail inválido")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Senha obrigatória")]
    [DataType(DataType.Password)]
    public string Senha { get; set; } = "";

    public bool LembrarMe { get; set; }
}

public class DashboardViewModel
{
    public int TotalClientes { get; set; }
    public int TarefasPendentes { get; set; }
    public int TarefasAtrasadas { get; set; }
    public int AprovacoesPendentes { get; set; }
    public int ConteudosAtrasados { get; set; }
    public decimal ReceitaMes { get; set; }
    public decimal DespesaMes { get; set; }
    public decimal LucroMes => ReceitaMes - DespesaMes;

    // Dashboard Cliente
    public int? ClienteId { get; set; }
    public string? NomeCliente { get; set; }
    public int ConteudosMes { get; set; }
    public int ConteudosAprovados { get; set; }
    public int ConteudosPublicados { get; set; }

    public List<Tarefa> UltimasTarefas { get; set; } = [];
    public List<Conteudo> UltimosConteudos { get; set; } = [];
}
