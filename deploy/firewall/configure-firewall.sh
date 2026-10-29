#!/usr/bin/env bash
#
# Configurazione del firewall per UPS Control Center (§9.4).
#
# PROBLEMA NOTO: Docker scrive direttamente in iptables (catena DOCKER)
# scavalcando UFW. Una porta pubblicata resta raggiungibile anche se UFW la
# nega. Per questo si usano tre barriere indipendenti:
#
#   1. binding della porta sull'IP specifico (nel docker-compose.yml)
#   2. regole nella catena DOCKER-USER, che Docker rispetta
#   3. filtro IP applicativo nel portale
#
# Questo script MOSTRA le regole e chiede conferma. Non tocca le regole
# esistenti e in particolare NON modifica nulla relativo alla porta 8085 (PeaNUT).
#
# Uso: ./configure-firewall.sh [--apply]

set -euo pipefail

WEB_PORT="${WEB_PORT:-8443}"
NUT_PORT="${NUT_PORT:-3493}"
DOCKER_BRIDGE="${DOCKER_BRIDGE:-172.17.0.0/16}"

ALLOWED_NETWORKS=(
    "10.0.0.0/24"
    "172.16.10.0/24"
    "192.168.98.0/24"
    "192.168.114.0/24"
)

MODE="dry-run"
[[ "${1:-}" == "--apply" ]] && MODE="apply"

info()  { printf '\033[0;36m[info]\033[0m  %s\n' "$*"; }
warn()  { printf '\033[0;33m[avviso]\033[0m %s\n' "$*"; }
error() { printf '\033[0;31m[errore]\033[0m %s\n' "$*" >&2; }

run() {
    printf '  $ %s\n' "$*"
    [[ "$MODE" == "apply" ]] && "$@"
}

show_current_state() {
    info "Stato attuale (sola lettura)"
    echo
    echo "--- UFW ---"
    ufw status numbered 2>/dev/null || echo "  UFW non disponibile"
    echo
    echo "--- catena DOCKER-USER ---"
    iptables -L DOCKER-USER -n --line-numbers 2>/dev/null \
        || echo "  catena non presente (Docker non avviato?)"
    echo
    echo "--- porte in ascolto ---"
    ss -tlnp 2>/dev/null | grep -E ":(${WEB_PORT}|${NUT_PORT}|8085|5432)" \
        || echo "  nessuna delle porte rilevanti è in ascolto"
    echo
    warn "La porta 8085 appartiene a PeaNUT: nessuna regola di questo script la riguarda."
}

show_rules() {
    echo
    info "Regole proposte"
    echo
    echo "# ---- Barriera 2: catena DOCKER-USER (necessaria perché Docker scavalca UFW) ----"
    echo "# Le regole di RETURN consentono le reti aziendali; la DROP finale blocca tutto il resto."
    echo

    for net in "${ALLOWED_NETWORKS[@]}"; do
        run iptables -I DOCKER-USER -p tcp --dport "$WEB_PORT" -s "$net" -j RETURN
    done

    echo
    echo "# Traffico interno fra container: non deve essere bloccato dalla regola finale."
    run iptables -I DOCKER-USER -s "$DOCKER_BRIDGE" -d "$DOCKER_BRIDGE" -j RETURN

    echo
    echo "# Blocco finale per la porta del portale: va APPESA in coda (-A), non inserita."
    run iptables -A DOCKER-USER -p tcp --dport "$WEB_PORT" -j DROP

    echo
    echo "# ---- UFW: solo per le porte NON gestite da Docker (NUT sull'host) ----"
    for net in "${ALLOWED_NETWORKS[@]}"; do
        run ufw allow from "$net" to any port "$NUT_PORT" proto tcp comment "NUT da rete aziendale"
    done

    echo
    echo "# ---- Persistenza: le regole iptables NON sopravvivono al riavvio ----"
    echo "# Senza questo passo, dopo un reboot il portale resterebbe esposto."
    echo "  $ apt-get install -y iptables-persistent"
    echo "  $ netfilter-persistent save"
}

main() {
    echo "=============================================================="
    echo " UPS Control Center — configurazione firewall"
    echo "=============================================================="
    echo

    if [[ "$MODE" == "dry-run" ]]; then
        warn "MODALITÀ ANTEPRIMA: nessuna regola verrà applicata."
    else
        if [[ "$EUID" -ne 0 ]]; then
            error "L'applicazione richiede privilegi di root. Usare sudo."
            exit 1
        fi
        warn "MODALITÀ APPLICAZIONE: le regole del firewall verranno modificate."
    fi
    echo

    show_current_state
    show_rules

    if [[ "$MODE" == "apply" ]]; then
        echo
        warn "Verranno applicate le regole mostrate sopra."
        warn "Un errore nelle regole del firewall può interrompere l'accesso SSH:"
        warn "mantenere una sessione aperta durante l'operazione."
        echo
        read -r -p "Procedere? Digitare 'applica' per confermare: " answer
        if [[ "$answer" != "applica" ]]; then
            info "Operazione annullata. Nessuna modifica effettuata."
            exit 0
        fi
        info "Regole applicate. Ricordare di renderle persistenti."
    else
        echo
        info "Anteprima completata. Per applicare: sudo $0 --apply"
    fi

    echo
    info "Verifica consigliata dopo l'applicazione:"
    echo "  # da una rete autorizzata"
    echo "  curl -k https://10.0.0.8:${WEB_PORT}/health/live"
    echo "  # da una rete non autorizzata: la connessione deve essere rifiutata"
    echo "  # PeaNUT deve continuare a rispondere"
    echo "  curl -s -o /dev/null -w '%{http_code}' http://10.0.0.8:8085/"
}

main "$@"
