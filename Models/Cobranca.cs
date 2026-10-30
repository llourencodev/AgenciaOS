namespace AgenciaOS.Models;

public enum Periodicidade { Mensal, Anual }

public enum FormaPagamento { Pix, Boleto, CartaoCredito, Indefinida }

/// <summary>Plano comercial. Cadastrado pelo superadmin no painel (sem alterar código).</summary>
public class Plano
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nome { get; set; } = "";
    public string? Descricao { get; set; }
    public decimal Preco { get; set; }
    public Periodicidade Periodicidade { get; set; } = Periodicidade.Mensal;
    /// <summary>null = sem limite.</summary>
    public int? LimiteUsuarios { get; set; }
    public int? LimiteClientes { get; set; }
    public int? LimiteArmazenamentoMb { get; set; }
    /// <summary>Abas liberadas (CSV com os mesmos nomes de Usuario.AbasPermitidas). null/vazio = todas.</summary>
    public string? Modulos { get; set; }
    /// <summary>Texto livre exibido na vitrine (um recurso por linha).</summary>
    public string? RecursosAdicionais { get; set; }
    /// <summary>Disponível para novas contratações.</summary>
    public bool Ativo { get; set; } = true;
    public int Ordem { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> ListaModulos =>
        Modulos?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

/// <summary>
/// Assinatura de uma agência num gateway. A situação vigente é espelhada em Agencia.SituacaoAssinatura,
/// que é o que o bloqueio consulta a cada requisição.
/// </summary>
public class Assinatura : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public int PlanoId { get; set; }
    public Plano? Plano { get; set; }
    public SituacaoAssinatura Situacao { get; set; } = SituacaoAssinatura.PagamentoPendente;
    public FormaPagamento FormaPagamento { get; set; }
    public decimal Valor { get; set; }
    public string Gateway { get; set; } = "";
    public string? GatewayClienteId { get; set; }
    public string? GatewayAssinaturaId { get; set; }
    public DateTime? ProximoVencimento { get; set; }
    /// <summary>Fim do período de tolerância após um atraso.</summary>
    public DateTime? ToleranciaAte { get; set; }
    /// <summary>Cancelamento pedido: acesso continua até o fim do período pago.</summary>
    public DateTime? CanceladaEm { get; set; }
    public DateTime? AcessoAte { get; set; }
    public bool Vigente { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public ICollection<Pagamento> Pagamentos { get; set; } = [];
}

public enum StatusPagamento { Pendente, Confirmado, Recebido, Vencido, Estornado, Removido, Contestado }

/// <summary>Cobrança gerada pelo gateway (histórico financeiro da assinatura).</summary>
public class Pagamento : IPertenceAgencia
{
    public int Id { get; set; }
    public int AgenciaId { get; set; }
    public int AssinaturaId { get; set; }
    public Assinatura? Assinatura { get; set; }
    public string GatewayPagamentoId { get; set; } = "";
    public decimal Valor { get; set; }
    public decimal? ValorLiquido { get; set; }
    public StatusPagamento Status { get; set; } = StatusPagamento.Pendente;
    public FormaPagamento FormaPagamento { get; set; }
    public DateTime? Vencimento { get; set; }
    public DateTime? PagoEm { get; set; }
    public string? UrlFatura { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public bool Quitado => Status is StatusPagamento.Confirmado or StatusPagamento.Recebido;
}

/// <summary>Evento recebido de um gateway. (Gateway, EventoId) único = idempotência.</summary>
public class EventoWebhook
{
    public long Id { get; set; }
    public string Gateway { get; set; } = "";
    public string EventoId { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime RecebidoEm { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessadoEm { get; set; }
    public int Tentativas { get; set; }
    public string? Erro { get; set; }
}
