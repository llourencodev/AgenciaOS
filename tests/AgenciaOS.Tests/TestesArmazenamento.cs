using System.Text;
using AgenciaOS.Models;
using AgenciaOS.Services.Armazenamento;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgenciaOS.Tests;

internal static class Amostras
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    public static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF");
    public static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0, 0, 0];

    public static ArmazenamentoOptions Opcoes(string? caminho = null) => new()
    {
        CaminhoLocal = caminho ?? Path.Combine(Path.GetTempPath(), "agenciaos-tests", Guid.NewGuid().ToString("N")),
        TamanhoMaximoBytes = 1024 * 1024
    };
}

public class TestesValidacaoArquivos
{
    private readonly ValidadorArquivos _v = new(Amostras.Opcoes());

    [Fact]
    public void Aceita_arquivo_cujo_conteudo_corresponde_a_extensao()
    {
        var r = _v.Validar("foto.PNG", 100, Amostras.Png);
        Assert.True(r.Valido);
        Assert.Equal("image/png", r.Mime); // MIME definido pelo servidor, não pelo navegador
        Assert.True(_v.Validar("contrato.pdf", 100, Amostras.Pdf).Valido);
    }

    [Theory]
    [InlineData("virus.png")]   // executável renomeado
    [InlineData("virus.pdf")]
    [InlineData("virus.txt")]
    public void Recusa_executavel_disfarcado(string nome) => Assert.False(_v.Validar(nome, 100, Amostras.Exe).Valido);

    [Fact]
    public void Recusa_extensao_fora_da_lista_tamanho_excessivo_e_conteudo_trocado()
    {
        Assert.False(_v.Validar("script.exe", 100, Amostras.Exe).Valido);
        Assert.False(_v.Validar("pagina.html", 100, Encoding.ASCII.GetBytes("<html>")).Valido);
        Assert.False(_v.Validar("sem-extensao", 100, Amostras.Pdf).Valido);
        Assert.False(_v.Validar("foto.png", 2 * 1024 * 1024, Amostras.Png).Valido);
        Assert.False(_v.Validar("foto.png", 100, Amostras.Pdf).Valido);
        Assert.False(_v.Validar("logo.svg", 100, Encoding.ASCII.GetBytes("<svg></svg>"), ValidadorArquivos.ExtensoesImagemMarca).Valido);
    }

    [Fact]
    public void Svg_e_aceito_mas_nunca_exibido_no_navegador()
    {
        var r = _v.Validar("arte.svg", 100, Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"));
        Assert.True(r.Valido);
        Assert.False(ValidadorArquivos.PodeExibirNoNavegador(r.Mime));
        Assert.True(ValidadorArquivos.PodeExibirNoNavegador("image/png"));
    }

    [Fact]
    public void Nome_seguro_remove_caminho_e_caracteres_perigosos()
    {
        Assert.Equal("passwd", ValidadorArquivos.NomeSeguro("../../etc/passwd"));
        Assert.Equal("relatorio.pdf", ValidadorArquivos.NomeSeguro("C:\\temp\\relatorio.pdf"));
        Assert.Equal("ab.pdf", ValidadorArquivos.NomeSeguro("a\"b\r\n.pdf"));
        var cd = ValidadorArquivos.ContentDisposition("Relatório \"final\".pdf", inline: false);
        Assert.StartsWith("attachment; filename=\"Relat_rio final.pdf\"", cd);
        Assert.Contains("filename*=UTF-8''Relat%C3%B3rio%20final.pdf", cd);
    }

    [Theory]
    [InlineData("agencies/1/../2/clients/3/x.pdf")]
    [InlineData("../agencies/1/x.pdf")]
    [InlineData("agencies/x/clients/1.pdf")]
    [InlineData("outros/1/x.pdf")]
    [InlineData("agencies/1//x.pdf")]
    public void Chave_fora_do_espaco_da_agencia_e_recusada(string chave) =>
        Assert.Throws<ArgumentException>(() => ChavesArmazenamento.Validar(chave));

    [Fact]
    public void Chave_gerada_fica_no_espaco_da_agencia_e_do_cliente()
    {
        var chave = ChavesArmazenamento.ArquivoCliente(7, 42, ".PDF", new DateTime(2026, 10, 30));
        Assert.Matches("^agencies/7/clients/42/2026/10/[a-f0-9]{32}\\.pdf$", chave);
        ChavesArmazenamento.Validar(chave);
    }
}

public class TestesArmazenamentoLocal
{
    [Fact]
    public async Task Grava_le_e_exclui_fora_do_wwwroot()
    {
        var opt = Amostras.Opcoes();
        var local = new ArmazenamentoLocal(opt);
        const string chave = "agencies/1/clients/1/2026/10/abc.pdf";

        await local.SalvarAsync(chave, new MemoryStream(Amostras.Pdf), "application/pdf");
        Assert.True(await local.ExisteAsync(chave));
        await using (var s = await local.AbrirAsync(chave))
        {
            using var ms = new MemoryStream();
            await s!.CopyToAsync(ms);
            Assert.Equal(Amostras.Pdf, ms.ToArray());
        }
        Assert.Null(local.GerarUrlTemporaria(chave, TimeSpan.FromMinutes(1), "a.pdf", "application/pdf", false)); // streaming pela aplicação
        await local.ExcluirAsync(chave);
        Assert.Null(await local.AbrirAsync(chave));
        Assert.Throws<ArgumentException>(() => local.Caminho("agencies/1/../../../segredo.txt"));
    }

    [Fact]
    public async Task Servico_grava_somente_arquivos_validos_e_calcula_hash()
    {
        var opt = Amostras.Opcoes();
        var local = new ArmazenamentoLocal(opt);
        var servico = new ServicoArquivos(local, new ValidadorArquivos(opt));

        var ok = await servico.GravarAsync(new MemoryStream(Amostras.Pdf), "Contrato.pdf", Amostras.Pdf.Length, ext => ChavesArmazenamento.ArquivoCliente(1, 2, ext));
        Assert.NotNull(ok.Arquivo);
        Assert.Equal(await ServicoArquivos.HashAsync(new MemoryStream(Amostras.Pdf)), ok.Arquivo!.HashSha256);
        Assert.True(await local.ExisteAsync(ok.Arquivo.Chave));

        string? chaveRecusada = null;
        var ruim = await servico.GravarAsync(new MemoryStream(Amostras.Exe), "foto.png", Amostras.Exe.Length, ext => chaveRecusada = ChavesArmazenamento.ArquivoCliente(1, 2, ext));
        Assert.Null(ruim.Arquivo);
        Assert.NotNull(ruim.Erro);
        Assert.Null(chaveRecusada); // nem chegou a gerar chave/gravar

        // Tamanho real acima do limite, mesmo que o tamanho declarado minta
        var grande = new byte[2 * 1024 * 1024];
        Amostras.Pdf.CopyTo(grande, 0);
        Assert.Null((await servico.GravarAsync(new MemoryStream(grande), "a.pdf", 10, ext => ChavesArmazenamento.ArquivoCliente(1, 2, ext))).Arquivo);
    }
}

/// <summary>
/// Provedor S3 contra um servidor S3 de verdade (local: container adobe/s3mock na porta 9090).
/// Configurável por AGENCIAOS_TEST_S3_ENDPOINT / _BUCKET / _KEY / _SECRET (ex.: um bucket R2 de homologação).
/// </summary>
[Trait("Categoria", "Integracao")]
public class TestesArmazenamentoS3
{
    private static ArmazenamentoS3 Criar() => new(new ArmazenamentoOptions
    {
        Provedor = "s3",
        S3Endpoint = Environment.GetEnvironmentVariable("AGENCIAOS_TEST_S3_ENDPOINT") ?? "http://127.0.0.1:9090",
        S3Bucket = Environment.GetEnvironmentVariable("AGENCIAOS_TEST_S3_BUCKET") ?? "agenciaos-test",
        S3AccessKey = Environment.GetEnvironmentVariable("AGENCIAOS_TEST_S3_KEY") ?? "teste",
        S3SecretKey = Environment.GetEnvironmentVariable("AGENCIAOS_TEST_S3_SECRET") ?? "teste",
        S3Regiao = "us-east-1",
    });

    [Fact]
    public async Task Upload_download_url_assinada_e_exclusao()
    {
        using var s3 = Criar();
        var chave = ChavesArmazenamento.ArquivoCliente(1, 1, ".pdf");

        await s3.SalvarAsync(chave, new MemoryStream(Amostras.Pdf), "application/pdf");
        Assert.True(await s3.ExisteAsync(chave));

        await using (var s = await s3.AbrirAsync(chave))
        {
            using var ms = new MemoryStream();
            await s!.CopyToAsync(ms);
            Assert.Equal(Amostras.Pdf, ms.ToArray());
        }

        var url = s3.GerarUrlTemporaria(chave, TimeSpan.FromMinutes(5), "Relatório.pdf", "application/pdf", inline: false);
        Assert.NotNull(url);
        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("X-Amz-Expires=300", url);
        using var http = new HttpClient();
        var resp = await http.GetAsync(url);
        Assert.True(resp.IsSuccessStatusCode, $"GET da URL assinada retornou {(int)resp.StatusCode}");
        Assert.Equal(Amostras.Pdf, await resp.Content.ReadAsByteArrayAsync());

        await s3.ExcluirAsync(chave);
        Assert.False(await s3.ExisteAsync(chave));
        Assert.Null(await s3.AbrirAsync(chave));
    }
}

public class TestesMigracaoArquivos
{
    private sealed class Ambiente(string raiz) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = raiz;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AgenciaOS";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = raiz;
        public string EnvironmentName { get; set; } = "Testing";
    }

    [Fact]
    public async Task Migra_com_verificacao_de_hash_e_so_remove_original_depois()
    {
        var legado = Path.Combine(Path.GetTempPath(), "agenciaos-legado", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(legado, "uploads", "1"));
        Directory.CreateDirectory(Path.Combine(legado, "brand"));
        await File.WriteAllBytesAsync(Path.Combine(legado, "uploads", "1", "a.pdf"), Amostras.Pdf);
        await File.WriteAllBytesAsync(Path.Combine(legado, "brand", "logo.png"), Amostras.Png);

        using var banco = new BancoTeste();
        int clienteId, okId, ausenteId;
        using (var db = banco.ContextoSistema())
        {
            var c = new Cliente { AgenciaId = 1, NomeEmpresa = "C", Responsavel = "r", Email = "e" };
            db.Clientes.Add(c);
            db.Configuracoes.Add(new Configuracao { AgenciaId = 1, LogoUrl = "/brand/logo.png" });
            await db.SaveChangesAsync();
            var ok = new Arquivo { AgenciaId = 1, ClienteId = c.Id, Nome = "a.pdf", NomeOriginal = "a.pdf", Url = "/uploads/1/a.pdf", TipoMime = "application/pdf", TamanhoBytes = Amostras.Pdf.Length };
            var ausente = new Arquivo { AgenciaId = 1, ClienteId = c.Id, Nome = "x.pdf", NomeOriginal = "x.pdf", Url = "/uploads/1/sumiu.pdf", TipoMime = "application/pdf" };
            var malicioso = new Arquivo { AgenciaId = 1, ClienteId = c.Id, Nome = "y", NomeOriginal = "y", Url = "/uploads/../../segredo.txt" };
            db.Arquivos.AddRange(ok, ausente, malicioso);
            await db.SaveChangesAsync();
            (clienteId, okId, ausenteId) = (c.Id, ok.Id, ausente.Id);
        }

        var opt = Amostras.Opcoes();
        var destino = new ArmazenamentoLocal(opt);
        MigracaoArquivosService.Relatorio rel;
        using (var db = banco.ContextoSistema())
            rel = await new MigracaoArquivosService(db, destino, new Ambiente(legado), NullLogger<MigracaoArquivosService>.Instance)
                .ExecutarAsync(removerOriginais: true, raizLegado: legado);

        Assert.Equal(1, rel.Migrados);
        Assert.Equal(2, rel.Ausentes); // o inexistente e o com caminho fora de uploads/
        Assert.Equal(0, rel.Falhas);
        Assert.Equal(1, rel.OriginaisRemovidos);
        Assert.Equal(1, rel.LogosMigrados);
        Assert.False(File.Exists(Path.Combine(legado, "uploads", "1", "a.pdf")));
        Assert.True(File.Exists(Path.Combine(legado, "brand", "logo.png"))); // logos originais preservados

        using (var db = banco.ContextoSistema())
        {
            var ok = await db.Arquivos.SingleAsync(a => a.Id == okId);
            Assert.StartsWith($"agencies/1/clients/{clienteId}/", ok.ChaveArmazenamento);
            Assert.Equal("local", ok.Provedor);
            Assert.Equal(await ServicoArquivos.HashAsync(new MemoryStream(Amostras.Pdf)), ok.HashSha256);
            Assert.Equal("/uploads/1/a.pdf", ok.Url); // caminho antigo preservado para auditoria
            await using var s = await destino.AbrirAsync(ok.ChaveArmazenamento!);
            Assert.Equal(ok.HashSha256, await ServicoArquivos.HashAsync(s!));

            Assert.Null((await db.Arquivos.SingleAsync(a => a.Id == ausenteId)).ChaveArmazenamento);
            Assert.Matches("^/marca/1/logo-[a-f0-9]{32}\\.png$", (await db.Configuracoes.SingleAsync()).LogoUrl!);
        }

        // Reexecutar é seguro (idempotente)
        using (var db = banco.ContextoSistema())
        {
            var de_novo = await new MigracaoArquivosService(db, destino, new Ambiente(legado), NullLogger<MigracaoArquivosService>.Instance)
                .ExecutarAsync(removerOriginais: true, raizLegado: legado);
            Assert.Equal(0, de_novo.Migrados);
            Assert.Equal(1, de_novo.JaMigrados);
        }
    }
}
