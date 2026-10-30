using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class LgpdAuditoriaPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Isenta",
                table: "Agencias",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MotivoSuspensao",
                table: "Agencias",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConfiguracoesPlataforma",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    DiasTeste = table.Column<int>(type: "integer", nullable: false),
                    DiasTolerancia = table.Column<int>(type: "integer", nullable: false),
                    EmailSuporte = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EncarregadoNome = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    EncarregadoEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExigirAceiteTermos = table.Column<bool>(type: "boolean", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracoesPlataforma", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosAuditoria",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Quando = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsuarioId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UsuarioEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AgenciaId = table.Column<int>(type: "integer", nullable: true),
                    Acao = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Entidade = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    EntidadeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Detalhes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosAuditoria", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SolicitacoesPrivacidade",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Protocolo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AgenciaId = table.Column<int>(type: "integer", nullable: true),
                    UsuarioId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    NomeTitular = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    EmailTitular = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Descricao = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Resposta = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CriadaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PrazoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConcluidaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AtendidaPorId = table.Column<string>(type: "text", nullable: true),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitacoesPrivacidade", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitacoesPrivacidade_Agencias_AgenciaId",
                        column: x => x.AgenciaId,
                        principalTable: "Agencias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_AgenciaId_Quando",
                table: "RegistrosAuditoria",
                columns: new[] { "AgenciaId", "Quando" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_Quando",
                table: "RegistrosAuditoria",
                column: "Quando");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitacoesPrivacidade_AgenciaId",
                table: "SolicitacoesPrivacidade",
                column: "AgenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitacoesPrivacidade_Protocolo",
                table: "SolicitacoesPrivacidade",
                column: "Protocolo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SolicitacoesPrivacidade_Status_PrazoEm",
                table: "SolicitacoesPrivacidade",
                columns: new[] { "Status", "PrazoEm" });

            // Decisão do proprietário (30/10/2026): a agência fundadora (Id 1, Social Unânime) é isenta de assinatura
            migrationBuilder.Sql("""UPDATE "Agencias" SET "Isenta" = true, "SituacaoAssinatura" = 'Ativa' WHERE "Id" = 1;""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfiguracoesPlataforma");

            migrationBuilder.DropTable(
                name: "RegistrosAuditoria");

            migrationBuilder.DropTable(
                name: "SolicitacoesPrivacidade");

            migrationBuilder.DropColumn(
                name: "Isenta",
                table: "Agencias");

            migrationBuilder.DropColumn(
                name: "MotivoSuspensao",
                table: "Agencias");
        }
    }
}
