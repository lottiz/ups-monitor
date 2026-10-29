#!/usr/bin/env bash
#
# Backup logico di PostgreSQL con retention (§20).
#
# Uso:
#   backup-postgres.sh           # un backup singolo
#   backup-postgres.sh --loop    # ciclo giornaliero (usato dal container ups-db-backup)

set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-/backups}"
RETENTION_DAYS="${RETENTION_DAYS:-30}"
DB_NAME="${PGDATABASE:-upscc}"

log() { printf '[%s] %s\n' "$(date -Is)" "$*"; }

do_backup() {
    mkdir -p "$BACKUP_DIR"

    local stamp file
    stamp="$(date +%Y%m%d-%H%M%S)"
    file="${BACKUP_DIR}/${DB_NAME}-${stamp}.dump"

    log "Backup di ${DB_NAME} in ${file}"

    # Formato custom (-Fc): compresso, ripristinabile selettivamente con pg_restore.
    # Si scrive su un file temporaneo e si rinomina solo a successo, così nella directory
    # non compaiono mai backup incompleti che sembrerebbero validi (§20).
    if pg_dump -Fc --no-owner --no-privileges -f "${file}.partial" "$DB_NAME"; then
        mv "${file}.partial" "$file"
        log "Backup completato: $(du -h "$file" | cut -f1)"
    else
        log "ERRORE: backup non riuscito, file parziale rimosso."
        rm -f "${file}.partial"
        return 1
    fi

    # Verifica di leggibilità: un backup non verificato non è un backup (§20).
    if pg_restore --list "$file" >/dev/null 2>&1; then
        log "Verifica di integrità superata."
    else
        log "ERRORE: il backup appena creato non è leggibile da pg_restore."
        return 1
    fi

    log "Rimozione dei backup più vecchi di ${RETENTION_DAYS} giorni"
    find "$BACKUP_DIR" -name "${DB_NAME}-*.dump" -type f \
        -mtime "+${RETENTION_DAYS}" -print -delete || true

    log "Backup presenti: $(find "$BACKUP_DIR" -name "${DB_NAME}-*.dump" -type f | wc -l)"
}

if [[ "${1:-}" == "--loop" ]]; then
    log "Servizio di backup avviato. Intervallo: 24 ore. Retention: ${RETENTION_DAYS} giorni."
    while true; do
        do_backup || log "Backup non riuscito: si riprova al ciclo successivo."
        sleep 86400
    done
else
    do_backup
fi
