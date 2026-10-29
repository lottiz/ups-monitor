#!/usr/bin/env bash
#
# Backup consistente del database SQLite del Safety Agent (§20).
#
# Usa VACUUM INTO: copiare il file con WAL attivo produrrebbe un backup
# potenzialmente incoerente, perché le transazioni non ancora riversate nel
# database principale risiedono nel file -wal.

set -euo pipefail

DB_PATH="${DB_PATH:-/var/lib/ups-control-center/safety.db}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/ups-control-center}"
RETENTION_DAYS="${RETENTION_DAYS:-30}"

log() { printf '[%s] %s\n' "$(date -Is)" "$*"; }

if [[ ! -f "$DB_PATH" ]]; then
    log "Database non trovato: $DB_PATH"
    exit 1
fi

if ! command -v sqlite3 >/dev/null 2>&1; then
    log "ERRORE: sqlite3 non installato. Installare con: apt-get install sqlite3"
    exit 1
fi

mkdir -p "$BACKUP_DIR"

stamp="$(date +%Y%m%d-%H%M%S)"
dest="${BACKUP_DIR}/safety-${stamp}.db"

log "Backup consistente di ${DB_PATH} in ${dest}"

# VACUUM INTO crea una copia compatta e transazionalmente coerente senza
# interrompere l'agent, che resta l'unico scrittore.
sqlite3 "$DB_PATH" "VACUUM INTO '${dest}';"

# Verifica di integrità del backup appena prodotto.
result="$(sqlite3 "$dest" "PRAGMA integrity_check;")"
if [[ "$result" != "ok" ]]; then
    log "ERRORE: verifica di integrità non superata: ${result}"
    rm -f "$dest"
    exit 1
fi

chmod 0640 "$dest"
log "Backup completato e verificato: $(du -h "$dest" | cut -f1)"

log "Rimozione dei backup più vecchi di ${RETENTION_DAYS} giorni"
find "$BACKUP_DIR" -name 'safety-*.db' -type f -mtime "+${RETENTION_DAYS}" -print -delete || true
