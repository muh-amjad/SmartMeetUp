#!/usr/bin/env bash
# Nightly database backup. Installed as a cron job by provision.sh; run from the repo root.
#
# Restore with:
#   gunzip -c backups/<file>.sql.gz | docker exec -i smartmeetup-postgres psql -U postgres -d smartmeetupdb

set -euo pipefail

BACKUP_DIR=${BACKUP_DIR:-backups}
RETENTION_DAYS=${RETENTION_DAYS:-7}
CONTAINER=smartmeetup-postgres
STAMP=$(date -u +%Y%m%d-%H%M%S)
TARGET="$BACKUP_DIR/smartmeetupdb-$STAMP.sql.gz"

mkdir -p "$BACKUP_DIR"

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  echo "[$(date -u +%FT%TZ)] $CONTAINER is not running; nothing to back up." >&2
  exit 1
fi

echo "[$(date -u +%FT%TZ)] Backing up to $TARGET"

# Write to a temporary name first so a failure part-way through cannot leave a truncated file that
# looks like a usable backup.
docker exec "$CONTAINER" pg_dump -U postgres --clean --if-exists smartmeetupdb \
  | gzip > "$TARGET.partial"

mv "$TARGET.partial" "$TARGET"

echo "[$(date -u +%FT%TZ)] Wrote $(du -h "$TARGET" | cut -f1)"

# Prune old dumps. Recordings are pruned separately by the bucket's own lifecycle rule.
find "$BACKUP_DIR" -name 'smartmeetupdb-*.sql.gz' -type f -mtime +"$RETENTION_DAYS" -print -delete

echo "[$(date -u +%FT%TZ)] Done. $(find "$BACKUP_DIR" -name 'smartmeetupdb-*.sql.gz' | wc -l) backup(s) retained."
