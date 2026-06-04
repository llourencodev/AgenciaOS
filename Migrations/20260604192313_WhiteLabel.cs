using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class WhiteLabel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Configuracoes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NomeAgencia = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Tagline = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LogoMarcaUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorPrimaria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorPrimariaDark = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorSecundaria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorTextoSobrePrimaria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CorSidebar = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GradienteIcone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GradienteSaudacao = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Configuracoes", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Configuracoes");
        }
    }
}
