#!/bin/sh
# Backup diário do Mídia+ no servidor: banco (pg_dump -Fc) + arquivos (volume storage).
#
# Agendar (crontab -e):
#   0 3 * * * sh /srv/apps/AgenciaOS/deploy/servidor/backup.sh >> /srv/backup/midiaplus/backup.log 2>&1
#
# Variáveis opcionais:
#   BACKUP_DIR            padrão /srv/backup/midiaplus (HD de backups)
#   BACKUP_RETENCAO_DIAS  padrão 30
set -eu

PASTA="$(cd "$(dirname "$0")" && pwd)"
BACKUP_DIR="${BACKUP_DIR:-/srv/backup/midiaplus}"
BACKUP_RETENCAO_DIAS="${BACKUP_RETENCAO_DIAS:-30}"
carimbo="$(date +%Y%m%d-%H%M%S)"

mkdir -p "$BACKUP_DIR"
cd "$PASTA"

echo "[$(date -Is)] Iniciando backup"

# Banco: formato custom (comprimido). Usuário/banco vêm do próprio container.
dump="$BACKUP_DIR/banco-$carimbo.dump"
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc --no-owner --no-privileges' > "$dump"
if [ ! -s "$dump" ]; then
    echo "ERRO: dump vazio" >&2
    rm -f "$dump"
    exit 1
fi

# Arquivos enviados pelos usuários (volume storage do app)
arquivos="$BACKUP_DIR/arquivos-$carimbo.tar.gz"
docker run --rm -v midiaplus_storage:/dados:ro alpine tar -czf - -C /dados . > "$arquivos"

sha256sum "$dump" "$arquivos" > "$BACKUP_DIR/sha256-$carimbo.txt"

# Retenção
find "$BACKUP_DIR" -maxdepth 1 -type f \( -name 'banco-*.dump' -o -name 'arquivos-*.tar.gz' -o -name 'sha256-*.txt' \) \
    -mtime +"$BACKUP_RETENCAO_DIAS" -delete

echo "[$(date -Is)] OK: $(du -h "$dump" | cut -f1) banco, $(du -h "$arquivos" | cut -f1) arquivos"
