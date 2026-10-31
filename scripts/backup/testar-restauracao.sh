#!/bin/sh
# Teste de restauração: um backup só conta como válido depois de restaurado e conferido.
#
# Restaura o dump num banco SEPARADO e VAZIO (nunca o de produção), confere o SHA-256 e compara as
# contagens com o manifesto gerado no backup. Sai com código diferente de zero se qualquer etapa falhar.
#
# Uso: testar-restauracao.sh <arquivo.dump>
#   RESTORE_DATABASE_URL   banco de destino descartável (obrigatório; será limpo)
set -eu

dump="${1:?informe o arquivo .dump}"
: "${RESTORE_DATABASE_URL:?defina RESTORE_DATABASE_URL (banco descartável)}"
manifesto="${dump%.dump}.manifest.txt"

if [ "${DATABASE_URL:-}" = "$RESTORE_DATABASE_URL" ]; then
  echo "Recusado: RESTORE_DATABASE_URL é o mesmo banco do backup." >&2; exit 2
fi
case "$RESTORE_DATABASE_URL" in
  *prod*) echo "Recusado: RESTORE_DATABASE_URL parece apontar para produção." >&2; exit 2 ;;
esac

if [ -f "$dump.sha256" ]; then
  esperado="$(cat "$dump.sha256")"
  atual="$(sha256sum "$dump" | cut -d' ' -f1)"
  [ "$esperado" = "$atual" ] || { echo "ERRO: SHA-256 não confere." >&2; exit 1; }
  echo "SHA-256 ok" >&2
fi

echo "Limpando o banco de teste e restaurando..." >&2
psql "$RESTORE_DATABASE_URL" -q -c "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;"
pg_restore --dbname="$RESTORE_DATABASE_URL" --no-owner --no-privileges --exit-on-error "$dump"

falhas=0
if [ -f "$manifesto" ]; then
  while IFS='|' read -r tabela esperado; do
    if [ "$tabela" = "migration" ]; then
      atual="$(psql "$RESTORE_DATABASE_URL" -At -c "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\"")"
    else
      atual="$(psql "$RESTORE_DATABASE_URL" -At -c "SELECT count(*) FROM \"$tabela\"")"
    fi
    if [ "$atual" = "$esperado" ]; then
      echo "ok   $tabela = $atual" >&2
    else
      echo "ERRO $tabela: esperado $esperado, restaurado $atual" >&2
      falhas=$((falhas + 1))
    fi
  done < "$manifesto"
else
  echo "Aviso: manifesto não encontrado; conferindo só se as tabelas existem." >&2
  psql "$RESTORE_DATABASE_URL" -At -c "SELECT count(*) FROM \"__EFMigrationsHistory\"" > /dev/null
fi

# Integridade básica após a restauração (mesmas regras do script de validação da migração multi-tenant)
orfaos="$(psql "$RESTORE_DATABASE_URL" -At -c "
  SELECT (SELECT count(*) FROM \"Tarefas\" t JOIN \"Clientes\" c ON c.\"Id\" = t.\"ClienteId\" WHERE t.\"AgenciaId\" <> c.\"AgenciaId\")
       + (SELECT count(*) FROM \"Conteudos\" t JOIN \"Clientes\" c ON c.\"Id\" = t.\"ClienteId\" WHERE t.\"AgenciaId\" <> c.\"AgenciaId\")")"
[ "$orfaos" = "0" ] || { echo "ERRO: $orfaos registros com agência inconsistente." >&2; falhas=$((falhas + 1)); }

if [ "$falhas" -gt 0 ]; then
  echo "RESTAURAÇÃO REPROVADA ($falhas problema(s))." >&2
  exit 1
fi
echo "RESTAURAÇÃO APROVADA: $dump" >&2
