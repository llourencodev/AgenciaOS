using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AgenciaOS.Migrations
{
    /// <inheritdoc />
    public partial class MultiTenancyAgencias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Migração aditiva da instalação de agência única para multi-tenant.
            // Ver docs/architecture/DATABASE_MIGRATION.md. Nenhum dado existente é apagado ou recriado.

            // 1) Tabela de agências
            migrationBuilder.CreateTable(
                name: "Agencias",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Nome = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    NomeFantasia = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Cnpj = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: true),
                    EmailPrincipal = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Telefone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SituacaoAssinatura = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlanoCodigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TesteExpiraEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CriadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AtualizadoEm = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agencias", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agencias_Slug",
                table: "Agencias",
                column: "Slug",
                unique: true);

            // 2) Agência atual = Id 1, com o nome que já está na configuração
            migrationBuilder.Sql("""
                INSERT INTO "Agencias" ("Id", "Nome", "Slug", "Status", "SituacaoAssinatura", "CriadoEm", "AtualizadoEm")
                SELECT 1, s.nome,
                       COALESCE(NULLIF(left(trim(both '-' from regexp_replace(lower(translate(s.nome,
                           'ÁÀÂÃÄáàâãäÉÈÊËéèêëÍÌÎÏíìîïÓÒÔÕÖóòôõöÚÙÛÜúùûüÇçÑñ',
                           'AAAAAaaaaaEEEEeeeeIIIIiiiiOOOOOoooooUUUUuuuuCcNn')), '[^a-z0-9]+', '-', 'g')), 60), ''), 'agencia-principal'),
                       'Ativa', 'Ativa', now(), now()
                FROM (SELECT COALESCE((SELECT "NomeAgencia" FROM "Configuracoes" ORDER BY "Id" LIMIT 1), 'Agência principal') AS nome) s;
                SELECT setval(pg_get_serial_sequence('"Agencias"', 'Id'), 1);
                """);

            // 3-5) Coluna anulável → preenche com a agência 1 → NOT NULL → índice → FK (RESTRICT: excluir agência nunca apaga dados)
            foreach (var tabela in TabelasDaAgencia)
            {
                migrationBuilder.AddColumn<int>(name: "AgenciaId", table: tabela, type: "integer", nullable: true);
                migrationBuilder.Sql($"""UPDATE "{tabela}" SET "AgenciaId" = 1 WHERE "AgenciaId" IS NULL;""");
                migrationBuilder.AlterColumn<int>(name: "AgenciaId", table: tabela, type: "integer", nullable: false,
                    oldClrType: typeof(int), oldType: "integer", oldNullable: true);
                migrationBuilder.CreateIndex(name: $"IX_{tabela}_AgenciaId", table: tabela, column: "AgenciaId");
                migrationBuilder.AddForeignKey(name: $"FK_{tabela}_Agencias_AgenciaId", table: tabela, column: "AgenciaId",
                    principalTable: "Agencias", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            }

            // Usuários: todos os atuais são da agência 1; NULL fica reservado ao superadmin da plataforma
            migrationBuilder.AddColumn<int>(name: "AgenciaId", table: "AspNetUsers", type: "integer", nullable: true);
            migrationBuilder.Sql("""UPDATE "AspNetUsers" SET "AgenciaId" = 1;""");
            migrationBuilder.CreateIndex(name: "IX_AspNetUsers_AgenciaId", table: "AspNetUsers", column: "AgenciaId");
            migrationBuilder.AddForeignKey(name: "FK_AspNetUsers_Agencias_AgenciaId", table: "AspNetUsers", column: "AgenciaId",
                principalTable: "Agencias", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        private static readonly string[] TabelasDaAgencia =
        [
            "Clientes", "Conteudos", "Tarefas", "Arquivos", "Financeiros", "Contratos", "DatasComemorativas",
            "Configuracoes", "ConexoesMarketing", "ContasMarketing", "RelatoriosMarketing"
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Arquivos_Agencias_AgenciaId",
                table: "Arquivos");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Agencias_AgenciaId",
                table: "AspNetUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_Clientes_Agencias_AgenciaId",
                table: "Clientes");

            migrationBuilder.DropForeignKey(
                name: "FK_ConexoesMarketing_Agencias_AgenciaId",
                table: "ConexoesMarketing");

            migrationBuilder.DropForeignKey(
                name: "FK_Configuracoes_Agencias_AgenciaId",
                table: "Configuracoes");

            migrationBuilder.DropForeignKey(
                name: "FK_ContasMarketing_Agencias_AgenciaId",
                table: "ContasMarketing");

            migrationBuilder.DropForeignKey(
                name: "FK_Conteudos_Agencias_AgenciaId",
                table: "Conteudos");

            migrationBuilder.DropForeignKey(
                name: "FK_Contratos_Agencias_AgenciaId",
                table: "Contratos");

            migrationBuilder.DropForeignKey(
                name: "FK_DatasComemorativas_Agencias_AgenciaId",
                table: "DatasComemorativas");

            migrationBuilder.DropForeignKey(
                name: "FK_Financeiros_Agencias_AgenciaId",
                table: "Financeiros");

            migrationBuilder.DropForeignKey(
                name: "FK_RelatoriosMarketing_Agencias_AgenciaId",
                table: "RelatoriosMarketing");

            migrationBuilder.DropForeignKey(
                name: "FK_Tarefas_Agencias_AgenciaId",
                table: "Tarefas");

            migrationBuilder.DropTable(
                name: "Agencias");

            migrationBuilder.DropIndex(
                name: "IX_Tarefas_AgenciaId",
                table: "Tarefas");

            migrationBuilder.DropIndex(
                name: "IX_RelatoriosMarketing_AgenciaId",
                table: "RelatoriosMarketing");

            migrationBuilder.DropIndex(
                name: "IX_Financeiros_AgenciaId",
                table: "Financeiros");

            migrationBuilder.DropIndex(
                name: "IX_DatasComemorativas_AgenciaId",
                table: "DatasComemorativas");

            migrationBuilder.DropIndex(
                name: "IX_Contratos_AgenciaId",
                table: "Contratos");

            migrationBuilder.DropIndex(
                name: "IX_Conteudos_AgenciaId",
                table: "Conteudos");

            migrationBuilder.DropIndex(
                name: "IX_ContasMarketing_AgenciaId",
                table: "ContasMarketing");

            migrationBuilder.DropIndex(
                name: "IX_Configuracoes_AgenciaId",
                table: "Configuracoes");

            migrationBuilder.DropIndex(
                name: "IX_ConexoesMarketing_AgenciaId",
                table: "ConexoesMarketing");

            migrationBuilder.DropIndex(
                name: "IX_Clientes_AgenciaId",
                table: "Clientes");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_AgenciaId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_Arquivos_AgenciaId",
                table: "Arquivos");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Tarefas");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "RelatoriosMarketing");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Financeiros");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "DatasComemorativas");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Contratos");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Conteudos");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "ContasMarketing");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Configuracoes");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "ConexoesMarketing");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Clientes");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AgenciaId",
                table: "Arquivos");
        }
    }
}
