#!/bin/sh
# Backup do PostgreSQL do AgenciaOS.
#
# Gera um dump no formato custom (pg_dump -Fc, já comprimido), um manifesto com contagens de linhas
# por tabela e o SHA-256 do arquivo. Se as variáveis S3_* estiverem definidas, envia para o bucket
# (Cloudflare R2 ou S3) e apaga backups mais antigos que BACKUP_RETENCAO_DIAS.
#
# Variáveis:
#   DATABASE_URL            postgresql://usuario:senha@host:porta/banco  (obrigatória)
#   BACKUP_DIR              pasta local de saída (padrão: ./backups)
#   BACKUP_PREFIXO          prefixo no bucket (padrão: backups/producao)
#   BACKUP_RETENCAO_DIAS    padrão 30
#   S3_ENDPOINT S3_BUCKET AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY  (opcionais, para envio)
#
# Saída: imprime o caminho do dump (última linha), usado pelo teste de restauração.
set -eu

: "${DATABASE_URL:?defina DATABASE_URL}"
BACKUP_DIR="${BACKUP_DIR:-./backups}"
BACKUP_PREFIXO="${BACKUP_PREFIXO:-backups/producao}"
BACKUP_RETENCAO_DIAS="${BACKUP_RETENCAO_DIAS:-30}"

mkdir -p "$BACKUP_DIR"
carimbo="$(date -u +%Y%m%dT%H%M%SZ)"
dump="$BACKUP_DIR/agenciaos-$carimbo.dump"
manifesto="$BACKUP_DIR/agenciaos-$carimbo.manifest.txt"

echo "Gerando dump..." >&2
pg_dump --dbname="$DATABASE_URL" -Fc --no-owner --no-privileges -f "$dump"

# Manifesto: contagem exata das tabelas principais, para conferir depois da restauração
psql "$DATABASE_URL" -At -F '|' -c "
  SELECT 'migration', max(\"MigrationId\") FROM \"__EFMigrationsHistory\"
  UNION ALL SELECT 'Agencias', count(*)::text FROM \"Agencias\"
  UNION ALL SELECT 'AspNetUsers', count(*)::text FROM \"AspNetUsers\"
  UNION ALL SELECT 'Clientes', count(*)::text FROM \"Clientes\"
  UNION ALL SELECT 'Tarefas', count(*)::text FROM \"Tarefas\"
  UNION ALL SELECT 'Conteudos', count(*)::text FROM \"Conteudos\"
  UNION ALL SELECT 'Financeiros', count(*)::text FROM \"Financeiros\"
  UNION ALL SELECT 'Arquivos', count(*)::text FROM \"Arquivos\"
  UNION ALL SELECT 'Assinaturas', count(*)::text FROM \"Assinaturas\"
  UNION ALL SELECT 'Pagamentos', count(*)::text FROM \"Pagamentos\"
  UNION ALL SELECT 'ConsentimentosTermos', count(*)::text FROM \"ConsentimentosTermos\";" > "$manifesto"

sha256sum "$dump" | cut -d' ' -f1 > "$dump.sha256"
tamanho="$(wc -c < "$dump")"
echo "Dump: $dump ($tamanho bytes, sha256 $(cat "$dump.sha256"))" >&2
if [ "$tamanho" -lt 1024 ]; then
  echo "ERRO: dump suspeito (menos de 1 KB)." >&2
  exit 1
fi

if [ -n "${S3_BUCKET:-}" ]; then
  : "${S3_ENDPOINT:?defina S3_ENDPOINT}"
  destino="s3://$S3_BUCKET/$BACKUP_PREFIXO"
  for f in "$dump" "$dump.sha256" "$manifesto"; do
    aws s3 cp "$f" "$destino/$(basename "$f")" --endpoint-url "$S3_ENDPOINT" --only-show-errors
  done
  echo "Enviado para $destino" >&2

  # Retenção: remove objetos mais antigos que BACKUP_RETENCAO_DIAS (pelo carimbo no nome)
  limite="$(date -u -d "-$BACKUP_RETENCAO_DIAS days" +%Y%m%d 2>/dev/null || date -u -v-"$BACKUP_RETENCAO_DIAS"d +%Y%m%d)"
  aws s3 ls "$destino/" --endpoint-url "$S3_ENDPOINT" | awk '{print $4}' | while read -r nome; do
    data="$(echo "$nome" | sed -n 's/^agenciaos-\([0-9]\{8\}\)T.*/\1/p')"
    if [ -n "$data" ] && [ "$data" -lt "$limite" ]; then
      aws s3 rm "$destino/$nome" --endpoint-url "$S3_ENDPOINT" --only-show-errors
      echo "Removido (retenção): $nome" >&2
    fi
  done
fi

echo "$dump"
