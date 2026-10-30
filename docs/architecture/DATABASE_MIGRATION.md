# Estratégia de migração do banco (Fase 1)

Migration: `Migrations/*_MultiTenancyAgencias.cs`.

## 1. Princípios

- Nenhum `DROP`/`DELETE` de dados existentes no `Up()`.
- IDs e relacionamentos atuais preservados; só colunas novas.
- Todos os registros existentes pertencem à agência atual, que vira a agência **Id 1**.
- A migration é **aditiva e em etapas** dentro de uma transação (o Npgsql roda cada migration em transação; se qualquer passo falhar, nada é aplicado).

## 2. Passos do `Up()`

1. `CREATE TABLE "Agencias"` (+ índice único em `Slug`).
2. Inserir a agência atual com Id 1:
   - `Nome` = `Configuracoes.NomeAgencia` da primeira linha, se existir; senão `'Agência principal'`.
   - `Slug` gerado a partir do nome (`social-unanime` no caso atual).
   - `Status = Ativa`, `SituacaoAssinatura = Ativa` (cliente fundador, não entra em teste).
   - Ajuste da sequence de `Agencias.Id` para continuar em 2.
3. Para cada tabela da agência: `ADD COLUMN "AgenciaId" integer NULL`.
4. `UPDATE <tabela> SET "AgenciaId" = 1 WHERE "AgenciaId" IS NULL`.
5. `ALTER COLUMN "AgenciaId" SET NOT NULL` (exceto `AspNetUsers`, que continua anulável para o superadmin).
6. Índices em `AgenciaId` e FKs para `Agencias(Id)` com `ON DELETE RESTRICT` (excluir agência nunca apaga dados em cascata).
7. `UPDATE "AspNetUsers" SET "AgenciaId" = 1` (todos os usuários atuais são da agência atual).

## 3. Validação de integridade (script)

Rodado depois da migration (local e homologação; produção só com aprovação):

```sql
-- 1) nenhuma linha sem agência
SELECT 'Clientes', count(*) FROM "Clientes" WHERE "AgenciaId" IS NULL
UNION ALL SELECT 'Tarefas', count(*) FROM "Tarefas" WHERE "AgenciaId" IS NULL;  -- ...repetir por tabela
-- 2) contagens antes = depois (comparar com o snapshot do backup)
-- 3) filhos na mesma agência do cliente
SELECT count(*) FROM "Tarefas" t JOIN "Clientes" c ON c."Id" = t."ClienteId" WHERE t."AgenciaId" <> c."AgenciaId";
SELECT count(*) FROM "Conteudos" t JOIN "Clientes" c ON c."Id" = t."ClienteId" WHERE t."AgenciaId" <> c."AgenciaId";
-- 4) usuários sem agência = só superadmins
SELECT "Email" FROM "AspNetUsers" WHERE "AgenciaId" IS NULL;
```

O script completo está em `docs/architecture/scripts/validar-migracao-tenant.sql`.

## 4. Backup e rollback

**Antes de aplicar em qualquer ambiente:**

```bash
# local (Docker)
docker exec agenciaos-postgres pg_dump -U agenciaos -d agenciaos -Fc -f /tmp/antes-multitenant.dump
docker cp agenciaos-postgres:/tmp/antes-multitenant.dump backups/
# produção (Railway) — SÓ com aprovação: pg_dump com a DATABASE_URL pública do Railway
```

**Validar o backup** restaurando em um banco separado antes de migrar:

```bash
docker exec agenciaos-postgres createdb -U agenciaos agenciaos_restore_test
docker exec agenciaos-postgres pg_restore -U agenciaos -d agenciaos_restore_test /tmp/antes-multitenant.dump
```

**Rollback**, em ordem de preferência:

1. `dotnet ef database update CentralInteligenciaMarketing` → executa o `Down()`, que remove FKs, índices, colunas `AgenciaId` e a tabela `Agencias`. Os dados originais não são tocados (só as colunas novas somem). Atenção: dados criados por **outras** agências depois da migração ficariam misturados; o rollback só é seguro enquanto existir apenas a agência 1.
2. Restaurar o dump com `pg_restore --clean`.

## 5. Ensaio realizado (local)

Registrado em `IMPLEMENTATION_ROADMAP.md` → "Diário da Fase 1": backup, restauração em banco separado, carga de dados de exemplo, aplicação da migration na cópia, validação, e só então no banco local principal.

## 6. Produção (Railway)

Não aplicado. Passos quando o usuário aprovar:

1. Janela de manutenção curta (a migration é rápida: só ADD COLUMN/UPDATE em tabelas pequenas).
2. `pg_dump` da produção + restauração de teste em banco separado.
3. Deploy da versão nova (o startup aplica a migration).
4. Rodar o script de validação.
5. Conferir login do admin e telas principais.
6. Definir `ADMIN_PASSWORD` não é mais necessário para usuários existentes; trocar a senha do admin se ainda for a padrão.
