using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class IconesMonocromados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorIconesInativos",
                table: "Configuracoes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IconesMonocromados",
                table: "Configuracoes",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorIconesInativos",
                table: "Configuracoes");

            migrationBuilder.DropColumn(
                name: "IconesMonocromados",
                table: "Configuracoes");
        }
    }
}
