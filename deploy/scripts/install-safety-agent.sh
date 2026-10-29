#!/usr/bin/env bash
#
# Installazione del Safety Agent su Ubuntu (§23).
#
# IMPORTANTE: questo script MOSTRA tutti i comandi e chiede conferma prima di
# modificare l'host, come richiesto da §25.20. Non tocca NUT, non tocca PeaNUT,
# non modifica UFW.
#
# Uso:
#   ./install-safety-agent.sh --dry-run    # mostra solo i comandi (predefinito)
#   ./install-safety-agent.sh --apply      # esegue, chiedendo conferma

set -euo pipefail

AGENT_USER="upsagent"
AGENT_GROUP="upsagent"
INSTALL_DIR="/opt/ups-control-center/safety-agent"
STATE_DIR="/var/lib/ups-control-center"
CONFIG_DIR="/etc/ups-control-center"
UNIT_FILE="/etc/systemd/system/ups-safety-agent.service"
BACKUP_DIR="/var/backups/ups-control-center"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

MODE="dry-run"
[[ "${1:-}" == "--apply" ]] && MODE="apply"

info()  { printf '\033[0;36m[info]\033[0m  %s\n' "$*"; }
warn()  { printf '\033[0;33m[avviso]\033[0m %s\n' "$*"; }
error() { printf '\033[0;31m[errore]\033[0m %s\n' "$*" >&2; }

# Esegue il comando in modalità --apply, lo stampa in dry-run.
run() {
    printf '  $ %s\n' "$*"
    if [[ "$MODE" == "apply" ]]; then
        "$@"
    fi
}

# ---------------------------------------------------------------- prerequisiti
check_prerequisites() {
    info "Verifica dei prerequisiti (sola lettura, nessuna modifica)."

    if [[ "$(uname -s)" != "Linux" ]]; then
        error "Questo script è destinato a Linux."
        exit 1
    fi

    if ! command -v systemctl >/dev/null 2>&1; then
        error "systemd non disponibile: il Safety Agent gira come servizio systemd."
        exit 1
    fi

    echo
    info "Sistema:"
    printf '  %s\n' "$(. /etc/os-release && echo "${PRETTY_NAME}")"
    printf '  kernel: %s\n' "$(uname -r)"
    printf '  CPU:    %s core\n' "$(nproc)"
    printf '  RAM:    %s\n' "$(free -h | awk '/^Mem:/ {print $2" totale, "$7" disponibili"}')"
    printf '  disco:  %s\n' "$(df -h /var | awk 'NR==2 {print $4" liberi su "$2}')"

    echo
    info "Servizi NUT (NON vengono modificati da questo script):"
    for svc in nut-driver@riello nut-server nut-monitor; do
        state="$(systemctl is-active "$svc" 2>/dev/null || true)"
        enabled="$(systemctl is-enabled "$svc" 2>/dev/null || true)"
        printf '  %-22s attivo=%-10s abilitato=%s\n' "$svc" "${state:-n.d.}" "${enabled:-n.d.}"
    done

    if [[ "$(systemctl is-enabled nut-monitor 2>/dev/null || true)" == "enabled" ]]; then
        warn "nut-monitor risulta abilitato. Il progetto prevede che resti disabilitato."
        warn "Questo script non lo modifica: verificare la configurazione dell'host."
    fi

    echo
    info "Lettura UPS (richiede il pacchetto nut-client):"
    if command -v upsc >/dev/null 2>&1; then
        if upsc riello@127.0.0.1 ups.status >/dev/null 2>&1; then
            printf '  ups.status  = %s\n' "$(upsc riello@127.0.0.1 ups.status 2>/dev/null)"
            printf '  ups.mfr     = %s\n' "$(upsc riello@127.0.0.1 ups.mfr 2>/dev/null || echo 'non disponibile')"
            printf '  ups.model   = %s\n' "$(upsc riello@127.0.0.1 ups.model 2>/dev/null || echo 'non disponibile')"
            printf '  runtime     = %s\n' "$(upsc riello@127.0.0.1 battery.runtime 2>/dev/null || echo 'non disponibile')"
        else
            warn "upsc non riesce a leggere riello@127.0.0.1: verificare upsd prima di procedere."
        fi
    else
        warn "upsc non installato: impossibile verificare l'UPS da qui."
    fi

    echo
    info "PeaNUT (non viene toccato):"
    if command -v docker >/dev/null 2>&1; then
        docker ps --filter "name=nut-peanut" --format '  {{.Names}}  {{.Status}}  {{.Ports}}' \
            2>/dev/null || warn "  stato non verificabile"
    fi
}

# ------------------------------------------------------------------ build
build_agent() {
    echo
    info "Compilazione del Safety Agent (self-contained, senza dipendenze .NET sull'host)."

    if ! command -v dotnet >/dev/null 2>&1; then
        error "SDK .NET non trovato. Compilare su un'altra macchina e copiare l'artefatto,"
        error "oppure installare l'SDK .NET 10."
        exit 1
    fi

    local out="${REPO_ROOT}/artifacts/safety-agent"

    run dotnet publish "${REPO_ROOT}/src/UPSControlCenter.SafetyAgent/UPSControlCenter.SafetyAgent.csproj" \
        -c Release -r linux-x64 --self-contained true \
        -p:PublishSingleFile=false \
        -o "$out"
}

# ------------------------------------------------------------------ install
install_agent() {
    echo
    warn "Da qui iniziano le MODIFICHE ALL'HOST."
    echo
    info "Comandi previsti:"
    echo

    echo "  # 1. Utente di servizio dedicato, senza shell"
    run getent group "$AGENT_GROUP" >/dev/null 2>&1 || run groupadd --system "$AGENT_GROUP"
    run id -u "$AGENT_USER" >/dev/null 2>&1 || run useradd --system \
        --gid "$AGENT_GROUP" --shell /usr/sbin/nologin \
        --home-dir "$STATE_DIR" --no-create-home "$AGENT_USER"

    echo
    echo "  # 2. Directory di stato, configurazione e backup"
    run install -d -m 0750 -o "$AGENT_USER" -g "$AGENT_GROUP" "$STATE_DIR"
    run install -d -m 0750 -o root -g "$AGENT_GROUP" "$CONFIG_DIR"
    run install -d -m 0750 -o "$AGENT_USER" -g "$AGENT_GROUP" "$BACKUP_DIR"
    run install -d -m 0755 -o root -g root "$INSTALL_DIR"

    echo
    echo "  # 3. Binari"
    run cp -a "${REPO_ROOT}/artifacts/safety-agent/." "$INSTALL_DIR/"
    run chmod 0755 "${INSTALL_DIR}/UPSControlCenter.SafetyAgent"
    run chown -R root:root "$INSTALL_DIR"

    echo
    echo "  # 4. File di configurazione con i segreti (da compilare a mano)"
    if [[ ! -f "${CONFIG_DIR}/safety-agent.env" ]]; then
        run install -m 0640 -o root -g "$AGENT_GROUP" \
            "${REPO_ROOT}/deploy/systemd/safety-agent.env.example" \
            "${CONFIG_DIR}/safety-agent.env"
        warn "Compilare ${CONFIG_DIR}/safety-agent.env con la chiave HMAC e il token."
        warn "Devono coincidere con SAFETY_CONFIG_HMAC_KEY e SAFETY_AGENT_TOKEN del .env Docker."
    else
        info "  ${CONFIG_DIR}/safety-agent.env esiste già: non viene sovrascritto."
    fi

    echo
    echo "  # 5. Unit systemd"
    run cp "${REPO_ROOT}/deploy/systemd/ups-safety-agent.service" "$UNIT_FILE"
    run chmod 0644 "$UNIT_FILE"
    run systemctl daemon-reload

    echo
    echo "  # 6. Abilitazione e avvio"
    run systemctl enable ups-safety-agent.service
    run systemctl start ups-safety-agent.service
}

# ------------------------------------------------------------------ main
main() {
    echo "=============================================================="
    echo " UPS Control Center — installazione del Safety Agent"
    echo "=============================================================="
    echo
    if [[ "$MODE" == "dry-run" ]]; then
        warn "MODALITÀ ANTEPRIMA: nessuna modifica verrà applicata."
        warn "Per eseguire realmente: $0 --apply"
    else
        warn "MODALITÀ APPLICAZIONE: l'host verrà modificato."
    fi
    echo

    check_prerequisites

    if [[ "$MODE" == "apply" ]]; then
        if [[ "$EUID" -ne 0 ]]; then
            error "L'applicazione richiede privilegi di root. Usare sudo."
            exit 1
        fi

        echo
        warn "Verranno eseguite le modifiche elencate di seguito."
        warn "NON vengono toccati: NUT, PeaNUT, UFW, i container Docker esistenti."
        echo
        read -r -p "Procedere? Digitare 'installa' per confermare: " answer
        if [[ "$answer" != "installa" ]]; then
            info "Operazione annullata. Nessuna modifica effettuata."
            exit 0
        fi
    fi

    build_agent
    install_agent

    echo
    if [[ "$MODE" == "apply" ]]; then
        info "Installazione completata. Verifiche consigliate:"
        echo "  systemctl status ups-safety-agent"
        echo "  journalctl -u ups-safety-agent -f"
        echo
        warn "L'agent parte in stato SafeHold finché non riceve una configurazione"
        warn "firmata dal portale: monitora e registra, ma non esegue azioni."
    else
        info "Anteprima completata. Nessuna modifica effettuata."
        echo "  Per eseguire: sudo $0 --apply"
    fi
}

main "$@"
