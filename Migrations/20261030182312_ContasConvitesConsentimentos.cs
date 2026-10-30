using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class ContasConvitesConsentimentos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsentimentosTermos",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UsuarioId = table.Column<string>(type: "text", nullable: false),
                    AgenciaId = table.Column<int>(type: "integer", nullable: true),
                    Documento = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Versao = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AceitoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentimentosTermos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConsentimentosTermos_Agencias_AgenciaId",
                        column: x => x.AgenciaId,
                        principalTable: "Agencias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConsentimentosTermos_AspNetUsers_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConvitesUsuario",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgenciaId = table.Column<int>(type: "integer", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Nome = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    AbasPermitidas = table.Column<string>(type: "text", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiraEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CriadoPorId = table.Column<string>(type: "text", nullable: true),
                    AceitoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevogadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConvitesUsuario", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConvitesUsuario_Agencias_AgenciaId",
                        column: x => x.AgenciaId,
                        principalTable: "Agencias",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConvitesUsuario_AspNetUsers_CriadoPorId",
                        column: x => x.CriadoPorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentimentosTermos_AgenciaId",
                table: "ConsentimentosTermos",
                column: "AgenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentimentosTermos_UsuarioId_Documento",
                table: "ConsentimentosTermos",
                columns: new[] { "UsuarioId", "Documento" });

            migrationBuilder.CreateIndex(
                name: "IX_ConvitesUsuario_AgenciaId",
                table: "ConvitesUsuario",
                column: "AgenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_ConvitesUsuario_CriadoPorId",
                table: "ConvitesUsuario",
                column: "CriadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConvitesUsuario_TokenHash",
                table: "ConvitesUsuario",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConsentimentosTermos");

            migrationBuilder.DropTable(
                name: "ConvitesUsuario");
        }
    }
}
