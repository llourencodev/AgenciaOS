using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class MelhoriasSistema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DataFimRecorrencia",
                table: "Tarefas",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrequenciaRecorrencia",
                table: "Tarefas",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Recorrente",
                table: "Tarefas",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Tipo",
                table: "Tarefas",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ContaFixa",
                table: "Financeiros",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "GrupoParcelamentoId",
                table: "Financeiros",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NumeroParcelas",
                table: "Financeiros",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParcelaAtual",
                table: "Financeiros",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Parcelado",
                table: "Financeiros",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ResponsavelId",
                table: "Financeiros",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AbasPermitidas",
                table: "AspNetUsers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Contratos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Titulo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Conteudo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Observacoes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Valor = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CriadoEm = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DataAssinatura = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DataVigencia = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClienteId = table.Column<int>(type: "int", nullable: false),
                    CriadoPorId = table.Column<string>(type: "nvarchar(450)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contratos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contratos_AspNetUsers_CriadoPorId",
                        column: x => x.CriadoPorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Contratos_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DatasComemorativas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Nome = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Descricao = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Dia = table.Column<int>(type: "int", nullable: false),
                    Mes = table.Column<int>(type: "int", nullable: false),
                    Anual = table.Column<bool>(type: "bit", nullable: false),
                    Ano = table.Column<int>(type: "int", nullable: true),
                    Cor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Tipo = table.Column<int>(type: "int", nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatasComemorativas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Financeiros_ResponsavelId",
                table: "Financeiros",
                column: "ResponsavelId");

            migrationBuilder.CreateIndex(
                name: "IX_Contratos_ClienteId",
                table: "Contratos",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_Contratos_CriadoPorId",
                table: "Contratos",
                column: "CriadoPorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Financeiros_AspNetUsers_ResponsavelId",
                table: "Financeiros",
                column: "ResponsavelId",
                principalTable: "AspNetUsers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Financeiros_AspNetUsers_ResponsavelId",
                table: "Financeiros");

            migrationBuilder.DropTable(
                name: "Contratos");

            migrationBuilder.DropTable(
                name: "DatasComemorativas");

            migrationBuilder.DropIndex(
                name: "IX_Financeiros_ResponsavelId",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "DataFimRecorrencia",
                table: "Tarefas");

            migrationBuilder.DropColumn(
                name: "FrequenciaRecorrencia",
                table: "Tarefas");

            migrationBuilder.DropColumn(
                name: "Recorrente",
                table: "Tarefas");

            migrationBuilder.DropColumn(
                name: "Tipo",
                table: "Tarefas");

            migrationBuilder.DropColumn(
                name: "ContaFixa",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "GrupoParcelamentoId",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "NumeroParcelas",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "ParcelaAtual",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "Parcelado",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "ResponsavelId",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "AbasPermitidas",
                table: "AspNetUsers");
        }
    }
}
