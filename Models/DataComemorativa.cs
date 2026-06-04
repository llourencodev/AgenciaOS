namespace AgenciaOS.Models;

public class DataComemorativa
{
    public int Id { get; set; }
    public string Nome { get; set; } = "";
    public string? Descricao { get; set; }
    public int Dia { get; set; }
    public int Mes { get; set; }
    public bool Anual { get; set; } = true;
    public int? Ano { get; set; }
    public string Cor { get; set; } = "#6366f1";
    public TipoDataComemorativa Tipo { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public DateTime? ProximaOcorrencia
    {
        get
        {
            var hoje = DateTime.Today;
            var ano = Anual ? hoje.Year : (Ano ?? hoje.Year);
            try
            {
                var data = new DateTime(ano, Mes, Dia);
                if (Anual && data < hoje) data = data.AddYears(1);
                return data;
            }
            catch { return null; }
        }
    }
}

public enum TipoDataComemorativa { Nacional, Regional, Comercial, Marketing, Outro }
