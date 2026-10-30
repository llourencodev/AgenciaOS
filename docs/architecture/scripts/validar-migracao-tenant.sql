-- Validação da migration MultiTenancyAgencias. Todas as consultas de "problemas" devem retornar 0.
-- Uso: psql -d <banco> -f validar-migracao-tenant.sql

\echo '== Agências =='
SELECT "Id", "Nome", "Slug", "Status", "SituacaoAssinatura" FROM "Agencias" ORDER BY "Id";

\echo '== Linhas por agência (comparar com as contagens antes da migração) =='
SELECT 'Clientes' t, "AgenciaId", count(*) FROM "Clientes" GROUP BY 2
UNION ALL SELECT 'Conteudos', "AgenciaId", count(*) FROM "Conteudos" GROUP BY 2
UNION ALL SELECT 'Tarefas', "AgenciaId", count(*) FROM "Tarefas" GROUP BY 2
UNION ALL SELECT 'Arquivos', "AgenciaId", count(*) FROM "Arquivos" GROUP BY 2
UNION ALL SELECT 'Financeiros', "AgenciaId", count(*) FROM "Financeiros" GROUP BY 2
UNION ALL SELECT 'Contratos', "AgenciaId", count(*) FROM "Contratos" GROUP BY 2
UNION ALL SELECT 'DatasComemorativas', "AgenciaId", count(*) FROM "DatasComemorativas" GROUP BY 2
UNION ALL SELECT 'Configuracoes', "AgenciaId", count(*) FROM "Configuracoes" GROUP BY 2
UNION ALL SELECT 'ConexoesMarketing', "AgenciaId", count(*) FROM "ConexoesMarketing" GROUP BY 2
UNION ALL SELECT 'ContasMarketing', "AgenciaId", count(*) FROM "ContasMarketing" GROUP BY 2
UNION ALL SELECT 'RelatoriosMarketing', "AgenciaId", count(*) FROM "RelatoriosMarketing" GROUP BY 2
UNION ALL SELECT 'AspNetUsers', "AgenciaId", count(*) FROM "AspNetUsers" GROUP BY 2
ORDER BY 1, 2;

\echo '== Problemas (todos devem ser 0) =='
SELECT 'filho em agência diferente do cliente' problema, (
    (SELECT count(*) FROM "Conteudos" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "Tarefas" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "Arquivos" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "Financeiros" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "Contratos" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "ConexoesMarketing" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "ContasMarketing" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
  + (SELECT count(*) FROM "RelatoriosMarketing" x JOIN "Clientes" c ON c."Id" = x."ClienteId" WHERE x."AgenciaId" <> c."AgenciaId")
) total
UNION ALL SELECT 'usuário de cliente em outra agência',
    (SELECT count(*) FROM "Clientes" c JOIN "AspNetUsers" u ON u."Id" = c."UsuarioClienteId" WHERE u."AgenciaId" IS DISTINCT FROM c."AgenciaId")
UNION ALL SELECT 'responsável de tarefa em outra agência',
    (SELECT count(*) FROM "Tarefas" t JOIN "AspNetUsers" u ON u."Id" = t."ResponsavelId" WHERE u."AgenciaId" IS DISTINCT FROM t."AgenciaId")
UNION ALL SELECT 'responsável financeiro em outra agência',
    (SELECT count(*) FROM "Financeiros" f JOIN "AspNetUsers" u ON u."Id" = f."ResponsavelId" WHERE u."AgenciaId" IS DISTINCT FROM f."AgenciaId")
UNION ALL SELECT 'usuário sem agência que não é SuperAdmin',
    (SELECT count(*) FROM "AspNetUsers" u WHERE u."AgenciaId" IS NULL AND NOT EXISTS (
        SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId" WHERE ur."UserId" = u."Id" AND r."Name" = 'SuperAdmin'));
