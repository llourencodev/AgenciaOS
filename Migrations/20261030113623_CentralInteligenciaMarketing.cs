using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class CentralInteligenciaMarketing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConexoesMarketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UsuarioExternoId = table.Column<string>(type: "text", nullable: true),
                    UsuarioExternoNome = table.Column<string>(type: "text", nullable: true),
                    TokenAcessoProtegido = table.Column<string>(type: "text", nullable: false),
                    TokenRefreshProtegido = table.Column<string>(type: "text", nullable: true),
                    ExpiraEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RefreshExpiraEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Escopos = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UltimoErro = table.Column<string>(type: "text", nullable: true),
                    ConectadoPorId = table.Column<string>(type: "text", nullable: true),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConexoesMarketing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConexoesMarketing_AspNetUsers_ConectadoPorId",
                        column: x => x.ConectadoPorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ConexoesMarketing_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RelatoriosMarketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PeriodoInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodoFim = table.Column<DateOnly>(type: "date", nullable: false),
                    Plataformas = table.Column<string>(type: "text", nullable: false),
                    Indicadores = table.Column<string>(type: "text", nullable: true),
                    CompararPeriodoAnterior = table.Column<bool>(type: "boolean", nullable: false),
                    Periodicidade = table.Column<string>(type: "text", nullable: true),
                    EmailsDestino = table.Column<string>(type: "text", nullable: true),
                    GeradoPorId = table.Column<string>(type: "text", nullable: true),
                    GeradoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelatoriosMarketing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RelatoriosMarketing_AspNetUsers_GeradoPorId",
                        column: x => x.GeradoPorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RelatoriosMarketing_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContasMarketing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    ConexaoMarketingId = table.Column<int>(type: "integer", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdExterno = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Nome = table.Column<string>(type: "text", nullable: false),
                    NomeUsuario = table.Column<string>(type: "text", nullable: true),
                    FotoUrl = table.Column<string>(type: "text", nullable: true),
                    TipoConta = table.Column<string>(type: "text", nullable: true),
                    TokenRecursoProtegido = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    UltimoErro = table.Column<string>(type: "text", nullable: true),
                    MetricasIndisponiveis = table.Column<string>(type: "text", nullable: true),
                    Limitacao = table.Column<string>(type: "text", nullable: true),
                    UltimaSincronizacao = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProximaSincronizacao = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UltimaSolicitacaoManual = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FalhasConsecutivas = table.Column<int>(type: "integer", nullable: false),
                    Ativa = table.Column<bool>(type: "boolean", nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContasMarketing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContasMarketing_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContasMarketing_ConexoesMarketing_ConexaoMarketingId",
                        column: x => x.ConexaoMarketingId,
                        principalTable: "ConexoesMarketing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HistoricosSincronizacao",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContaMarketingId = table.Column<int>(type: "integer", nullable: false),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IniciadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinalizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Sucesso = table.Column<bool>(type: "boolean", nullable: false),
                    Tentativa = table.Column<int>(type: "integer", nullable: false),
                    RegistrosGravados = table.Column<int>(type: "integer", nullable: false),
                    Mensagem = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricosSincronizacao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HistoricosSincronizacao_ContasMarketing_ContaMarketingId",
                        column: x => x.ContaMarketingId,
                        principalTable: "ContasMarketing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricasDiarias",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContaMarketingId = table.Column<int>(type: "integer", nullable: false),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Data = table.Column<DateOnly>(type: "date", nullable: false),
                    Metrica = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Valor = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    Fonte = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ColetadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricasDiarias", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricasDiarias_ContasMarketing_ContaMarketingId",
                        column: x => x.ContaMarketingId,
                        principalTable: "ContasMarketing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricasDimensao",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContaMarketingId = table.Column<int>(type: "integer", nullable: false),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Data = table.Column<DateOnly>(type: "date", nullable: false),
                    Dimensao = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ValorDimensao = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Metrica = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Valor = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    ColetadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricasDimensao", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricasDimensao_ContasMarketing_ContaMarketingId",
                        column: x => x.ContaMarketingId,
                        principalTable: "ContasMarketing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicacoesMarketing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContaMarketingId = table.Column<int>(type: "integer", nullable: false),
                    ClienteId = table.Column<int>(type: "integer", nullable: false),
                    Plataforma = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdExterno = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Tipo = table.Column<string>(type: "text", nullable: true),
                    Legenda = table.Column<string>(type: "text", nullable: true),
                    Link = table.Column<string>(type: "text", nullable: true),
                    ThumbnailUrl = table.Column<string>(type: "text", nullable: true),
                    PublicadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Visualizacoes = table.Column<long>(type: "bigint", nullable: true),
                    Alcance = table.Column<long>(type: "bigint", nullable: true),
                    Curtidas = table.Column<long>(type: "bigint", nullable: true),
                    Comentarios = table.Column<long>(type: "bigint", nullable: true),
                    Compartilhamentos = table.Column<long>(type: "bigint", nullable: true),
                    Salvamentos = table.Column<long>(type: "bigint", nullable: true),
                    Cliques = table.Column<long>(type: "bigint", nullable: true),
                    Interacoes = table.Column<long>(type: "bigint", nullable: true),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicacoesMarketing", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicacoesMarketing_ContasMarketing_ContaMarketingId",
                        column: x => x.ContaMarketingId,
                        principalTable: "ContasMarketing",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConexoesMarketing_ClienteId_Plataforma",
                table: "ConexoesMarketing",
                columns: new[] { "ClienteId", "Plataforma" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConexoesMarketing_ConectadoPorId",
                table: "ConexoesMarketing",
                column: "ConectadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_ContasMarketing_Ativa_ProximaSincronizacao",
                table: "ContasMarketing",
                columns: new[] { "Ativa", "ProximaSincronizacao" });

            migrationBuilder.CreateIndex(
                name: "IX_ContasMarketing_ClienteId_Plataforma_IdExterno",
                table: "ContasMarketing",
                columns: new[] { "ClienteId", "Plataforma", "IdExterno" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContasMarketing_ConexaoMarketingId",
                table: "ContasMarketing",
                column: "ConexaoMarketingId");

            migrationBuilder.CreateIndex(
                name: "IX_HistoricosSincronizacao_ClienteId_IniciadoEm",
                table: "HistoricosSincronizacao",
                columns: new[] { "ClienteId", "IniciadoEm" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricosSincronizacao_ContaMarketingId",
                table: "HistoricosSincronizacao",
                column: "ContaMarketingId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricasDiarias_ClienteId_Plataforma_Data",
                table: "MetricasDiarias",
                columns: new[] { "ClienteId", "Plataforma", "Data" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricasDiarias_ContaMarketingId_Data_Metrica",
                table: "MetricasDiarias",
                columns: new[] { "ContaMarketingId", "Data", "Metrica" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricasDimensao_ClienteId_Dimensao_Data",
                table: "MetricasDimensao",
                columns: new[] { "ClienteId", "Dimensao", "Data" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricasDimensao_ContaMarketingId_Data_Dimensao_ValorDimens~",
                table: "MetricasDimensao",
                columns: new[] { "ContaMarketingId", "Data", "Dimensao", "ValorDimensao", "Metrica" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicacoesMarketing_ClienteId_PublicadoEm",
                table: "PublicacoesMarketing",
                columns: new[] { "ClienteId", "PublicadoEm" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicacoesMarketing_ContaMarketingId_IdExterno",
                table: "PublicacoesMarketing",
                columns: new[] { "ContaMarketingId", "IdExterno" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelatoriosMarketing_ClienteId",
                table: "RelatoriosMarketing",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_RelatoriosMarketing_GeradoPorId",
                table: "RelatoriosMarketing",
                column: "GeradoPorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricosSincronizacao");

            migrationBuilder.DropTable(
                name: "MetricasDiarias");

            migrationBuilder.DropTable(
                name: "MetricasDimensao");

            migrationBuilder.DropTable(
                name: "PublicacoesMarketing");

            migrationBuilder.DropTable(
                name: "RelatoriosMarketing");

            migrationBuilder.DropTable(
                name: "ContasMarketing");

            migrationBuilder.DropTable(
                name: "ConexoesMarketing");
        }
    }
}
