#!/usr/bin/env bash
#
# Kill switch di emergenza (§15, §8.3).
#
# Disabilita immediatamente tutte le automazioni mantenendo attivo il
# monitoraggio. Funziona anche con il portale e PostgreSQL indisponibili,
# perché agisce su un file locale che il Safety Agent controlla a ogni ciclo.
#
# Uso:
#   sudo ./disable-automation.sh "motivazione"
#   sudo ./disable-automation.sh --enable "motivazione"
#   ./disable-automation.sh --status

set -euo pipefail

STATE_DIR="${STATE_DIR:-/var/lib/ups-control-center}"
SENTINEL="${STATE_DIR}/AUTOMATION_DISABLED"

info()  { printf '\033[0;36m[info]\033[0m  %s\n' "$*"; }
warn()  { printf '\033[0;33m[avviso]\033[0m %s\n' "$*"; }
error() { printf '\033[0;31m[errore]\033[0m %s\n' "$*" >&2; }

show_status() {
    if [[ -f "$SENTINEL" ]]; then
        warn "AUTOMAZIONI DISABILITATE"
        echo
        cat "$SENTINEL"
    else
        info "Automazioni attive (nessun file sentinella presente)."
    fi
    echo
    info "Stato del Safety Agent:"
    systemctl is-active ups-safety-agent 2>/dev/null || echo "  non attivo o non installato"
}

case "${1:-}" in
    --status)
        show_status
        exit 0
        ;;
    --enable)
        reason="${2:-}"
        if [[ -z "$reason" ]]; then
            error "Specificare la motivazione della riabilitazione."
            exit 1
        fi
        if [[ "$EUID" -ne 0 ]]; then
            error "Operazione che richiede root. Usare sudo."
            exit 1
        fi

        rm -f "$SENTINEL"
        logger -t ups-control-center "Automazioni riabilitate da ${SUDO_USER:-root}: ${reason}"

        info "Blocco rimosso."
        warn "La modalità di esecuzione resta DRY_RUN: l'abilitazione di operazioni reali"
        warn "richiede la procedura completa dal portale (docs/08 §8.2)."
        exit 0
        ;;
    "")
        error "Specificare la motivazione."
        echo "Uso: $0 \"motivazione\" | $0 --enable \"motivazione\" | $0 --status"
        exit 1
        ;;
esac

reason="$1"

if [[ "$EUID" -ne 0 ]]; then
    error "Operazione che richiede root. Usare sudo."
    exit 1
fi

mkdir -p "$STATE_DIR"

# Scrittura atomica: un file sentinella parziale potrebbe essere letto a metà
# dall'agent mentre lo sta controllando.
tmp="${SENTINEL}.tmp.$$"
cat > "$tmp" <<EOF
disabled_at=$(date -Is)
by=${SUDO_USER:-root}
host=$(hostname)
reason=${reason}
EOF
chmod 0644 "$tmp"
mv -f "$tmp" "$SENTINEL"

logger -t ups-control-center "AUTOMAZIONI DISABILITATE da ${SUDO_USER:-root}: ${reason}"

echo
warn "AUTOMAZIONI DISABILITATE."
info "Il monitoraggio, lo storico e le notifiche restano attivi."
info "Nessuna sequenza di spegnimento o accensione verrà eseguita."
echo
info "Il Safety Agent recepisce il blocco al prossimo ciclo (entro ~10 secondi)."
info "Per aggiornare anche lo stato nel database, usare il portale o attendere"
info "la prossima sincronizzazione."
echo
info "Per riabilitare: sudo $0 --enable \"motivazione\""
