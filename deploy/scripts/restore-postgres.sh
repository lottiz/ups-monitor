#!/usr/bin/env bash
#
# Ripristino di PostgreSQL da un backup logico (§20).
#
# ATTENZIONE: operazione distruttiva. Mostra i comandi e richiede una conferma
# esplicita prima di procedere.
#
# Uso: restore-postgres.sh <file.dump> [--apply]

set -euo pipefail

DUMP_FILE="${1:-}"
MODE="dry-run"
[[ "${2:-}" == "--apply" ]] && MODE="apply"

COMPOSE_PROJECT="${COMPOSE_PROJECT:-ups-control-center}"
DB_CONTAINER="${DB_CONTAINER:-ups-db}"
DB_NAME="${POSTGRES_DB:-upscc}"
DB_USER="${POSTGRES_USER:-upscc}"

info()  { printf '\033[0;36m[info]\033[0m  %s\n' "$*"; }
warn()  { printf '\033[0;33m[avviso]\033[0m %s\n' "$*"; }
error() { printf '\033[0;31m[errore]\033[0m %s\n' "$*" >&2; }

if [[ -z "$DUMP_FILE" ]]; then
    error "Specificare il file di backup."
    echo "Uso: $0 <file.dump> [--apply]"
    echo
    echo "Backup disponibili:"
    find "${BACKUP_DIR:-/var/backups/ups-control-center}" -name '*.dump' -type f \
        -printf '  %TY-%Tm-%Td %TH:%TM  %10s  %p\n' 2>/dev/null | sort -r | head -20
    exit 1
fi

if [[ ! -f "$DUMP_FILE" ]]; then
    error "File non trovato: $DUMP_FILE"
    exit 1
fi

info "Verifica del backup prima di qualunque modifica"
if ! docker exec -i "$DB_CONTAINER" pg_restore --list /dev/stdin < "$DUMP_FILE" >/dev/null 2>&1; then
    warn "Impossibile verificare il dump tramite il container; si tenta la verifica locale."
    if command -v pg_restore >/dev/null 2>&1; then
        pg_restore --list "$DUMP_FILE" >/dev/null || {
            error "Il dump non è leggibile: ripristino annullato."
            exit 1
        }
    else
        warn "pg_restore non disponibile localmente: verifica non eseguita."
    fi
fi

echo
warn "Il ripristino SOVRASCRIVE il contenuto del database '${DB_NAME}'."
warn "I servizi applicativi vanno fermati prima, per evitare scritture concorrenti."
echo
info "Comandi previsti:"
cat <<COMMANDS
  # 1. Backup di sicurezza dello stato attuale (per poter tornare indietro)
  docker exec ${DB_CONTAINER} pg_dump -Fc -U ${DB_USER} ${DB_NAME} \\
      > /var/backups/ups-control-center/pre-restore-\$(date +%Y%m%d-%H%M%S).dump

  # 2. Arresto dei servizi applicativi (il database resta attivo)
  docker compose stop ups-web ups-orchestrator ups-notifications

  # 3. Ripristino
  docker exec -i ${DB_CONTAINER} pg_restore -U ${DB_USER} -d ${DB_NAME} \\
      --clean --if-exists --no-owner --no-privileges < ${DUMP_FILE}

  # 4. Riavvio
  docker compose up -d ups-web ups-orchestrator
COMMANDS

echo
warn "Il Safety Agent continua a funzionare durante tutta la procedura con la sua"
warn "ultima configurazione valida locale: la protezione non viene interrotta."
echo
warn "Dopo il ripristino, verificare che ExecutionMode sia DryRun: un dump proveniente"
warn "da un altro ambiente potrebbe contenere LIVE. La variabile UPS_ALLOW_LIVE impedisce"
warn "comunque l'attivazione, ma il valore va corretto (docs/08 §8.2)."

if [[ "$MODE" != "apply" ]]; then
    echo
    info "Anteprima: nessuna modifica effettuata. Per eseguire: $0 $DUMP_FILE --apply"
    exit 0
fi

echo
read -r -p "Procedere con il ripristino? Digitare 'ripristina' per confermare: " answer
if [[ "$answer" != "ripristina" ]]; then
    info "Operazione annullata."
    exit 0
fi

stamp="$(date +%Y%m%d-%H%M%S)"
safety="/var/backups/ups-control-center/pre-restore-${stamp}.dump"
mkdir -p "$(dirname "$safety")"

info "Backup di sicurezza in ${safety}"
docker exec "$DB_CONTAINER" pg_dump -Fc -U "$DB_USER" "$DB_NAME" > "$safety"

info "Arresto dei servizi applicativi"
docker compose stop ups-web ups-orchestrator ups-notifications || true

info "Ripristino in corso"
docker exec -i "$DB_CONTAINER" pg_restore -U "$DB_USER" -d "$DB_NAME" \
    --clean --if-exists --no-owner --no-privileges < "$DUMP_FILE"

info "Riavvio dei servizi"
docker compose up -d ups-web ups-orchestrator

info "Ripristino completato. Verificare /health/ready e la modalità di esecuzione."
