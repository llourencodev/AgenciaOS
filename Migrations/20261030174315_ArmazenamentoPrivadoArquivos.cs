using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class ArmazenamentoPrivadoArquivos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChaveArmazenamento",
                table: "Arquivos",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HashSha256",
                table: "Arquivos",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provedor",
                table: "Arquivos",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChaveArmazenamento",
                table: "Arquivos");

            migrationBuilder.DropColumn(
                name: "HashSha256",
                table: "Arquivos");

            migrationBuilder.DropColumn(
                name: "Provedor",
                table: "Arquivos");
        }
    }
}
