using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class DominiosAgencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DominiosAgencia",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgenciaId = table.Column<int>(type: "integer", nullable: false),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    TokenVerificacao = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Principal = table.Column<bool>(type: "boolean", nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerificadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UltimaTentativaEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UltimoErro = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CriadoPorId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DominiosAgencia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DominiosAgencia_Agencias_AgenciaId",
                        column: x => x.AgenciaId,
                        principalTable: "Agencias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DominiosAgencia_AgenciaId",
                table: "DominiosAgencia",
                column: "AgenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_DominiosAgencia_Host",
                table: "DominiosAgencia",
                column: "Host",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DominiosAgencia");
        }
    }
}
