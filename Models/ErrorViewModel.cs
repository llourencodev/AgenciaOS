namespace AgenciaOS.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public int StatusCode { get; set; } = 500;
    public string Titulo { get; set; } = "Algo deu errado";
    public string Mensagem { get; set; } = "Ocorreu um erro inesperado ao processar sua solicitação. Tente novamente; se continuar, avise o suporte informando o código abaixo.";

    /// <summary>Detalhe técnico da exceção: só preenchido em Development.</summary>
    public string? Detalhe { get; set; }

    public static (string Titulo, string Mensagem) TextosPorStatus(int status) => status switch
    {
        400 => ("Pedido inválido", "Os dados enviados não puderam ser processados. Volte, confira os campos e tente novamente."),
        401 => ("Sessão expirada", "Você precisa entrar novamente para continuar."),
        403 => ("Acesso negado", "Seu usuário não tem permissão para acessar esta página ou ação. Fale com o administrador da sua agência."),
        404 => ("Página não encontrada", "O endereço acessado não existe ou o registro foi removido."),
        405 => ("Ação não permitida", "Essa ação não pode ser feita por este caminho. Volte e use o botão da tela."),
        413 => ("Arquivo muito grande", "O arquivo enviado passa do tamanho permitido. Envie um arquivo menor."),
        429 => ("Muitas tentativas", "Foram feitas muitas tentativas em pouco tempo. Aguarde alguns minutos e tente novamente."),
        503 => ("Serviço indisponível", "O sistema está temporariamente indisponível. Tente novamente em instantes."),
        _ => ("Algo deu errado", "Ocorreu um erro inesperado ao processar sua solicitação. Tente novamente; se continuar, avise o suporte informando o código abaixo.")
    };
}
