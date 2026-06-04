using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class SidebarClara : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SidebarClara",
                table: "Configuracoes",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TamanhoLogo",
                table: "Configuracoes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SidebarClara",
                table: "Configuracoes");

            migrationBuilder.DropColumn(
                name: "TamanhoLogo",
                table: "Configuracoes");
        }
    }
}
