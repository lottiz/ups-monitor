#!/usr/bin/env bash
#
# Installazione end-to-end di UPS Control Center (docs/11), idempotente e sicura.
#
# Orchestra in sequenza: verifiche di sistema, preparazione e validazione rigorosa di .env,
# controlli preventivi (porte/risorse/container esistenti), un test TCP REALE della
# raggiungibilità di NUT dalla rete Docker del progetto, build/migrazioni/avvio del Control
# Plane, verifica dello STATO OPERATIVO EFFETTIVO (non dei soli log), installazione del Safety
# Agent, e un riepilogo finale.
#
# NON modifica mai NUT, PeaNUT, UFW o iptables. NON configura il firewall: resta un passo
# separato e manuale (deploy/firewall/configure-firewall.sh --apply). Se serve una riga LISTEN
# in upsd.conf, lo script la PROPONE con il gateway Docker rilevato al momento (mai un valore
# fisso presunto) ma non la esegue mai da solo.
#
# Lo script stesso non richiede di essere eseguito come root: solo l'installazione del Safety
# Agent (passo 7/8) richiede privilegi elevati, e usa sudo solo per quel passo, con una propria
# conferma esplicita e separata.
#
# Uso:
#   ./install-all.sh              # anteprima: mostra tutto, non modifica nulla
#   ./install-all.sh --apply      # esegue, chiedendo conferma ai punti critici
#
# Variabili opzionali per il test di PeaNUT (non obbligatorie in .env):
#   PEANUT_HOST, PEANUT_PORT (default: stesso host di NUT_HOST se è un IP reale, altrimenti
#   127.0.0.1, porta 8085) — vedi peanut_check_host().

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
ENV_FILE="${REPO_ROOT}/.env"
COMPOSE_FILE="${REPO_ROOT}/docker-compose.yml"

PROJECT_NAME="$(grep -m1 '^name:' "$COMPOSE_FILE" 2>/dev/null | awk '{print $2}')"
PROJECT_NAME="${PROJECT_NAME:-ups-control-center}"

MODE="dry-run"
[[ "${1:-}" == "--apply" ]] && MODE="apply"

info()  { printf '\033[0;36m[info]\033[0m  %s\n' "$*"; }
warn()  { printf '\033[0;33m[avviso]\033[0m %s\n' "$*"; }
error() { printf '\033[0;31m[errore]\033[0m %s\n' "$*" >&2; }

# Esegue il comando in modalità --apply, lo mostra soltanto in anteprima. Usato SOLO per i
# comandi che modificano davvero qualcosa (build/migrate/up/down): le verifiche di sola lettura
# chiamano i comandi direttamente, già protette da `[[ "$MODE" == "apply" ]]` a monte.
run() {
    printf '  $ %s\n' "$*"
    if [[ "$MODE" == "apply" ]]; then
        "$@"
    fi
}

confirm() {
    local prompt="$1" expected="$2" answer
    [[ "$MODE" != "apply" ]] && return 0
    read -r -p "$prompt" answer
    if [[ "$answer" != "$expected" ]]; then
        info "Operazione annullata. Nessuna modifica effettuata da questo punto in avanti."
        exit 0
    fi
}

# Wrapper centrale per tutte le chiamate a `docker compose` di questo progetto: un solo punto
# dove --project-name/--env-file/-f sono garantiti coerenti, e un solo punto da sostituire nei
# test (vedi test-install-all.sh).
dc() {
    docker compose --project-name "$PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" "$@"
}

# ============================================================== funzioni pure / testabili

env_get() {
    # Non fallisce mai (anche a .env assente o chiave mancante): restituisce stringa vuota.
    # Necessario perché viene quasi sempre usata in "v=$(env_get X)" sotto `set -e`.
    { [[ -f "$ENV_FILE" ]] && grep -E "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2-; } || true
}

is_valid_port() {
    [[ "$1" =~ ^[0-9]+$ ]] || return 1
    (( "$1" >= 1 && "$1" <= 65535 ))
}

is_valid_ipv4() {
    local ip="$1" n
    [[ "$ip" =~ ^([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})$ ]] || return 1
    for n in "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}" "${BASH_REMATCH[4]}"; do
        (( 10#$n <= 255 )) || return 1
    done
    return 0
}

# Porte/indirizzi già in ascolto sull'host. Non dipende da .env: riutilizzabile anche fuori
# contesto. Testabile sovrascrivendo `ss`.
port_in_use() {
    local address="$1" port="$2" line
    while IFS= read -r line; do
        case "$line" in
            "${address}:${port}" | "0.0.0.0:${port}" | "*:${port}" | "[::]:${port}")
                return 0 ;;
        esac
    done < <(ss -ltn 2>/dev/null | awk 'NR>1{print $4}')
    return 1
}

# Host su cui verificare PeaNUT. PeaNUT di norma gira sulla stessa macchina di NUT (docs/11
# esempio: 10.0.0.8), non necessariamente su 127.0.0.1 come assumeva la versione precedente di
# questo script. PEANUT_HOST in .env (opzionale, non in .env.example) ha sempre la precedenza.
peanut_check_host() {
    local v
    v="$(env_get PEANUT_HOST)"
    if [[ -n "$v" ]]; then
        echo "$v"
        return
    fi
    v="$(env_get NUT_HOST)"
    if [[ "$v" != "host.docker.internal" ]] && is_valid_ipv4 "$v"; then
        echo "$v"
        return
    fi
    echo "127.0.0.1"
}

# Nome REALE della rete Docker creata da compose per la chiave logica (es. "ups-internal").
# Mai derivato per convenzione (<project>_<key>): letto dalle etichette che compose assegna,
# perché il formato del nome non è un'API stabile.
compose_network_name() {
    local key="$1" project="$2"
    docker network ls --filter "label=com.docker.compose.project=${project}" \
        --filter "label=com.docker.compose.network=${key}" --format '{{.Name}}' 2>/dev/null | head -1
}

# Gateway REALE della rete Docker indicata, rilevato ora. Non esiste un indirizzo fisso
# garantito (172.17.0.1 è solo la rete "bridge" predefinita, non necessariamente quella usata
# da questo progetto, e Docker può assegnarne un'altra).
docker_network_gateway() {
    local network="$1"
    docker network inspect "$network" --format '{{(index .IPAM.Config 0).Gateway}}' 2>/dev/null
}

# Test TCP reale verso NUT, dalla stessa rete Docker e con lo stesso mapping
# host.docker.internal→host-gateway che useranno ups-web/ups-orchestrator. Non invia alcun
# comando NUT: è un semplice connect TCP, nessuna modifica a NUT.
nut_probe() {
    local network="$1" host="$2" port="$3"
    docker run --rm --network "$network" --add-host "host.docker.internal:host-gateway" \
        busybox:1.36 sh -c "nc -z -w3 '$host' '$port'" >/dev/null 2>&1
}

# Query di sola lettura sul database già in esecuzione. Isolata in una funzione per poterla
# sostituire nei test senza dover simulare l'intero `docker compose exec`.
db_query() {
    dc exec -T ups-db psql -At -U "$(env_get POSTGRES_USER)" -d "$(env_get POSTGRES_DB)" -c "$1" 2>/dev/null
}

# Verifica lo STATO OPERATIVO EFFETTIVO nel database, non una stringa nei log: l'unica fonte
# attendibile di cosa il sistema farebbe davvero (§8.2). Se ExecutionMode non è esattamente
# "DryRun", il deployment si interrompe: nessuna conferma può sovrascrivere questo vincolo.
assert_dryrun_enforced() {
    local mode adapters
    mode="$(db_query "SELECT value FROM ups.system_settings WHERE key='ExecutionMode'" | tr -d '[:space:]')"
    if [[ "$mode" != "DryRun" ]]; then
        error "ExecutionMode effettivo nel database è '${mode:-<vuoto/irraggiungibile>}', non 'DryRun'."
        return 1
    fi
    adapters="$(db_query "SELECT value FROM ups.system_settings WHERE key='LiveEnabledAdapters'" | tr -d '[:space:]')"
    if [[ -n "$adapters" ]]; then
        warn "LiveEnabledAdapters non è vuoto ('${adapters}') pur essendo ExecutionMode=DryRun: nessuna azione reale è comunque possibile, ma verificarlo appena entri nel portale."
    fi
    return 0
}

# Verifica che il job di migrazione abbia DAVVERO creato l'amministratore, prima di rimuovere
# BOOTSTRAP_ADMIN_PASSWORD da .env: altrimenti un fallimento silenzioso del seeding renderebbe
# impossibile accedere al portale e cancellerebbe anche la password necessaria per riprovare.
admin_was_created() {
    local email="$1" escaped found
    escaped=${email//\'/\'\'}
    found="$(db_query "SELECT 1 FROM ups.\"AspNetUsers\" WHERE email='${escaped}'" | tr -d '[:space:]')"
    [[ "$found" == "1" ]]
}

# Ferma i container SENZA rimuovere volumi (niente `-v`, niente `down --volumes`): i dati
# (PostgreSQL, chiavi di Data Protection) restano intatti per un nuovo tentativo.
rollback_preserving_data() {
    local reason="$1"
    error "$reason"
    warn "Arresto dei container con 'docker compose down' (SENZA rimuovere volumi)."
    dc down || true
    warn "Volumi NON toccati: ups-db-data, ups-dataprotection. NUT e PeaNUT non sono stati toccati."
    exit 1
}

# ---------------------------------------------------------- validazione rigorosa di .env (#4)
# Nessun valore della lista SECRET_KEYS viene mai stampato, in nessun ramo.
SECRET_KEYS=(POSTGRES_PASSWORD SAFETY_CONFIG_HMAC_KEY SAFETY_AGENT_TOKEN BOOTSTRAP_ADMIN_PASSWORD)

validate_env() {
    local errors=0 v

    [[ -f "$ENV_FILE" ]] || { error ".env non trovato in ${ENV_FILE}."; return 1; }

    v="$(env_get BIND_ADDRESS)"
    if [[ -z "$v" ]]; then
        error "BIND_ADDRESS non compilato."; errors=$((errors + 1))
    elif [[ "$v" == "0.0.0.0" ]]; then
        error "BIND_ADDRESS=0.0.0.0: esporrebbe il portale oltre l'IP del mini PC, scavalcando UFW (docs/09 §9.4)."
        errors=$((errors + 1))
    elif ! is_valid_ipv4 "$v"; then
        error "BIND_ADDRESS='${v}' non è un indirizzo IPv4 valido."; errors=$((errors + 1))
    fi

    v="$(env_get WEB_PORT)"
    is_valid_port "$v" || { error "WEB_PORT='${v}' non è una porta valida (1-65535)."; errors=$((errors + 1)); }

    v="$(env_get NUT_PORT)"
    is_valid_port "$v" || { error "NUT_PORT='${v}' non è una porta valida (1-65535)."; errors=$((errors + 1)); }

    v="$(env_get NUT_HOST)"
    [[ -n "$v" ]] || { error "NUT_HOST non compilato."; errors=$((errors + 1)); }

    v="$(env_get NUT_UPS_NAME)"
    [[ "$v" =~ ^[A-Za-z0-9_.-]+$ ]] || { error "NUT_UPS_NAME='${v}' non valido (solo lettere, cifre, '_.-')."; errors=$((errors + 1)); }

    v="$(env_get NUT_POLLING_SECONDS)"
    if ! { [[ "$v" =~ ^[0-9]+$ ]] && (( v >= 1 )); }; then
        error "NUT_POLLING_SECONDS='${v}' deve essere un intero positivo."; errors=$((errors + 1))
    fi

    v="$(env_get POSTGRES_DB)"
    [[ "$v" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || { error "POSTGRES_DB='${v}' non è un identificatore valido."; errors=$((errors + 1)); }

    v="$(env_get POSTGRES_USER)"
    [[ "$v" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || { error "POSTGRES_USER='${v}' non è un identificatore valido."; errors=$((errors + 1)); }

    v="$(env_get POSTGRES_PASSWORD)"
    if [[ -z "$v" ]]; then
        error "POSTGRES_PASSWORD non compilata."; errors=$((errors + 1))
    elif [[ "${#v}" -lt 16 ]]; then
        error "POSTGRES_PASSWORD troppo corta (minimo 16 caratteri)."; errors=$((errors + 1))
    fi

    v="$(env_get SAFETY_CONFIG_HMAC_KEY)"
    if [[ -z "$v" ]]; then
        error "SAFETY_CONFIG_HMAC_KEY non compilata."; errors=$((errors + 1))
    else
        local decoded_len
        decoded_len="$(printf '%s' "$v" | base64 -d 2>/dev/null | wc -c)"
        [[ "${decoded_len:-0}" -ge 32 ]] || { error "SAFETY_CONFIG_HMAC_KEY troppo corta o non base64 valida (minimo 32 byte decodificati)."; errors=$((errors + 1)); }
    fi

    v="$(env_get SAFETY_AGENT_TOKEN)"
    if [[ -z "$v" ]]; then
        error "SAFETY_AGENT_TOKEN non compilato."; errors=$((errors + 1))
    elif [[ "${#v}" -lt 20 ]]; then
        error "SAFETY_AGENT_TOKEN troppo corto (minimo 20 caratteri)."; errors=$((errors + 1))
    fi

    v="$(env_get BOOTSTRAP_ADMIN_EMAIL)"
    [[ "$v" =~ ^[^[:space:]@]+@[^[:space:]@]+\.[^[:space:]@]+$ ]] || { error "BOOTSTRAP_ADMIN_EMAIL='${v}' non è un'email valida."; errors=$((errors + 1)); }

    v="$(env_get BOOTSTRAP_ADMIN_PASSWORD)"
    if [[ -z "$v" ]]; then
        error "BOOTSTRAP_ADMIN_PASSWORD non compilata."; errors=$((errors + 1))
    else
        local pw_ok=1
        [[ "${#v}" -ge 12 ]] || pw_ok=0
        [[ "$v" =~ [A-Z] ]] || pw_ok=0
        [[ "$v" =~ [0-9] ]] || pw_ok=0
        [[ "$v" =~ [^A-Za-z0-9] ]] || pw_ok=0
        [[ "$pw_ok" -eq 1 ]] || { error "BOOTSTRAP_ADMIN_PASSWORD non soddisfa i requisiti minimi (12+ caratteri, almeno una maiuscola, una cifra, un simbolo)."; errors=$((errors + 1)); }
    fi

    v="$(env_get EXECUTION_MODE)"
    if [[ "$v" != "DryRun" ]]; then
        error "EXECUTION_MODE='${v}': deve essere esattamente 'DryRun'. L'attivazione LIVE si fa solo dal portale, mai da .env (vincolo di progetto, §8.2, §11 di questo script)."
        errors=$((errors + 1))
    fi

    v="$(env_get HOST_STATE_DIR)"
    [[ "$v" == /* ]] || { error "HOST_STATE_DIR='${v}' deve essere un percorso assoluto."; errors=$((errors + 1)); }

    v="$(env_get BACKUP_DIR)"
    [[ "$v" == /* ]] || { error "BACKUP_DIR='${v}' deve essere un percorso assoluto."; errors=$((errors + 1)); }

    v="$(env_get IMAGE_TAG)"
    [[ -n "$v" ]] || { error "IMAGE_TAG non compilato."; errors=$((errors + 1)); }

    if [[ "$errors" -gt 0 ]]; then
        error "${errors} problema/i in ${ENV_FILE}. Correggere prima di continuare."
        return 1
    fi

    info "Validazione di .env: tutti i controlli superati."
    print_env_summary
    return 0
}

# Riepilogo di .env con i soli valori non sensibili: per le chiavi in SECRET_KEYS stampa solo
# se sono presenti e la loro lunghezza, mai il contenuto.
print_env_summary() {
    local keys=(BIND_ADDRESS WEB_PORT NUT_HOST NUT_PORT NUT_UPS_NAME NUT_POLLING_SECONDS
        POSTGRES_DB POSTGRES_USER EXECUTION_MODE BOOTSTRAP_ADMIN_EMAIL IMAGE_TAG)
    local k v
    info "Riepilogo .env (nessun segreto stampato):"
    for k in "${keys[@]}"; do
        v="$(env_get "$k")"
        printf '    %-24s %s\n' "$k" "${v:-<vuoto>}"
    done
    for k in "${SECRET_KEYS[@]}"; do
        v="$(env_get "$k")"
        printf '    %-24s %s\n' "$k" "$([[ -n "$v" ]] && echo "impostato (${#v} caratteri)" || echo "<vuoto>")"
    done
}

# ================================================================== passi dello script

# ---------------------------------------------------------------- 1. verifiche di sistema
step_system_preflight() {
    echo
    echo "=== 1/8 — Verifiche di sistema (sola lettura) ==="

    if ! command -v docker >/dev/null 2>&1; then
        error "Docker non trovato. Installarlo prima di continuare."
        exit 1
    fi
    if ! docker compose version >/dev/null 2>&1; then
        error "Docker Compose (plugin v2) non trovato."
        exit 1
    fi
    if ! docker info >/dev/null 2>&1; then
        error "Docker non è utilizzabile da questo utente. Aggiungere l'utente al gruppo 'docker'"
        error "(sudo usermod -aG docker \$USER, poi ri-accedere) oppure eseguire con sudo."
        exit 1
    fi

    info "Risorse:"
    printf '  CPU:    %s core\n' "$(nproc)"
    printf '  RAM:    %s\n' "$(free -h | awk '/^Mem:/ {print $2" totale, "$7" disponibili"}')"
    printf '  disco:  %s\n' "$(df -h /opt 2>/dev/null | awk 'NR==2 {print $4" liberi su "$2}' || echo 'n.d.')"

    echo
    info "NUT (NON viene modificato da questo script):"
    local svc state enabled
    for svc in nut-server nut-monitor; do
        state="$(systemctl is-active "$svc" 2>/dev/null || echo 'n.d.')"
        enabled="$(systemctl is-enabled "$svc" 2>/dev/null || echo 'n.d.')"
        printf '  %-14s attivo=%-10s abilitato=%s\n' "$svc" "$state" "$enabled"
    done
    if [[ "$(systemctl is-enabled nut-monitor 2>/dev/null || true)" == "enabled" ]]; then
        warn "nut-monitor è abilitato: il progetto prevede che resti disabilitato. Non lo tocco."
    fi
}

# ---------------------------------------------------------------- 2. .env
step_env() {
    echo
    echo "=== 2/8 — Preparazione e validazione di .env ==="

    if [[ ! -f "$ENV_FILE" ]]; then
        info "Creo .env da .env.example."
        run cp "${REPO_ROOT}/.env.example" "$ENV_FILE"
        run chmod 600 "$ENV_FILE"
    else
        info ".env esiste già: non lo sovrascrivo."
    fi

    if [[ "$MODE" == "apply" ]]; then
        _fill_secret_if_empty() {
            local key="$1" bytes="$2"
            if grep -qE "^${key}=$" "$ENV_FILE"; then
                local value
                value="$(openssl rand -base64 "$bytes")"
                sed -i "s|^${key}=$|${key}=${value}|" "$ENV_FILE"
                info "Generato ${key} (non viene stampato: resta solo in .env, permessi 0600)."
            fi
        }
        _fill_secret_if_empty POSTGRES_PASSWORD 36
        _fill_secret_if_empty SAFETY_CONFIG_HMAC_KEY 48
        _fill_secret_if_empty SAFETY_AGENT_TOKEN 32
    else
        info "In anteprima non genero segreti: verrebbero generati con 'openssl rand -base64 N' solo per le chiavi vuote."
    fi

    echo
    warn "Valori che SOLO TU puoi scegliere correttamente — verificali in ${ENV_FILE}:"
    echo "  BIND_ADDRESS, WEB_PORT, NUT_HOST, NUT_PORT, NUT_UPS_NAME"
    echo "  BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD (solo per il primo avvio)"
    echo "  EXECUTION_MODE=DryRun  (non modificare: l'attivazione LIVE si fa solo dal portale)"

    if [[ "$MODE" == "apply" ]]; then
        if ! validate_env; then
            error "Correggere ${ENV_FILE} e rilanciare lo script."
            exit 1
        fi
    else
        info "In anteprima eseguo comunque la validazione di .env, se già presente:"
        validate_env || warn "(i problemi sopra andranno risolti prima di lanciare --apply)"
    fi
}

# ---------------------------------------------------- 3. controlli preventivi (porte/risorse)
step_resource_checks() {
    echo
    echo "=== 3/8 — Controlli preventivi (porte, risorse, container esistenti) ==="

    if [[ "$MODE" != "apply" ]]; then
        info "(controlli completi eseguiti solo in --apply: qui servirebbero i valori reali di .env)"
        return 0
    fi

    local bind_address web_port
    bind_address="$(env_get BIND_ADDRESS)"
    web_port="$(env_get WEB_PORT)"

    if port_in_use "$bind_address" "$web_port"; then
        error "${bind_address}:${web_port} è già in ascolto: il portale non potrà avviarsi su questa porta."
        error "Liberare la porta, oppure cambiare WEB_PORT in ${ENV_FILE}, prima di continuare."
        exit 1
    fi
    info "Porta ${bind_address}:${web_port} libera."

    local docker_root avail_gib
    docker_root="$(docker info --format '{{.DockerRootDir}}' 2>/dev/null || echo /var/lib/docker)"
    avail_gib="$(df --output=avail -BG "$docker_root" 2>/dev/null | tail -1 | tr -dc '0-9')"
    if [[ "${avail_gib:-0}" -lt 10 ]]; then
        error "Meno di 10 GiB liberi su ${docker_root} (dati Docker): rischio concreto di fallimento durante build o avvio."
        exit 1
    fi
    info "Spazio su ${docker_root}: ${avail_gib} GiB liberi."

    info "Container già presenti per questo progetto (vuoto = installazione nuova):"
    docker ps -a --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
        --format '  {{.Names}}  {{.Status}}' 2>/dev/null || true

    local peanut_host
    peanut_host="$(peanut_check_host)"
    info "PeaNUT (NON toccato), verifica su ${peanut_host}:8085:"
    curl -s -o /dev/null -w '  -> %{http_code}\n' "http://${peanut_host}:8085/" 2>/dev/null \
        || warn "  PeaNUT non raggiungibile su ${peanut_host}:8085 (verificare manualmente; non blocca questo deploy)."
}

# ---------------------------------------------------------------- 4. NUT: test reale
step_nut_connectivity() {
    echo
    echo "=== 4/8 — Collegamento a NUT dai container (test effettivo, nessuna modifica a NUT) ==="

    local nut_host nut_port
    nut_host="$(env_get NUT_HOST)"
    nut_port="$(env_get NUT_PORT)"

    if [[ "$MODE" != "apply" ]]; then
        info "In anteprima: creerei le reti del progetto (senza avviare servizi) e testerei"
        info "la connessione TCP a ${nut_host:-<NUT_HOST>}:${nut_port:-<NUT_PORT>} dalla rete Docker"
        info "del progetto, esattamente come farebbe ups-orchestrator — senza alcuna modifica a NUT."
        return 0
    fi

    info "Creo le reti/container del progetto (senza avviarli) per un test realistico…"
    if ! dc create >/dev/null; then
        error "Impossibile creare le risorse Docker del progetto. Verificare Docker e i permessi."
        exit 1
    fi

    local network
    network="$(compose_network_name ups-internal "$PROJECT_NAME")"
    if [[ -z "$network" ]]; then
        warn "Non riesco a determinare il nome della rete Docker del progetto: salto il test automatico."
        warn "Verificare manualmente: upsc ${nut_host:-<ups>}@${nut_port:-<porta>}"
        return 0
    fi
    info "Rete del progetto individuata: ${network}"

    if ! docker image inspect busybox:1.36 >/dev/null 2>&1; then
        info "Scarico un'immagine minima (busybox) solo per il test di connessione…"
        docker pull busybox:1.36 >/dev/null 2>&1 || true
    fi

    if ! docker image inspect busybox:1.36 >/dev/null 2>&1; then
        warn "Immagine di test non disponibile (offline?): non posso eseguire il test automatico."
        _suggest_nut_fix "$network" "$nut_host" "$nut_port"
        read -r -p "Hai verificato MANUALMENTE che NUT sia raggiungibile da qui? [s/N] " ans
        if [[ "$ans" != "s" && "$ans" != "S" ]]; then
            info "Mi fermo qui: verifica il collegamento a NUT e rilancia lo script."
            exit 0
        fi
        return 0
    fi

    if nut_probe "$network" "$nut_host" "$nut_port"; then
        info "Connessione TCP a ${nut_host}:${nut_port} riuscita dalla rete del progetto."
    else
        echo
        error "NUT (${nut_host}:${nut_port}) NON è raggiungibile dalla rete Docker del progetto."
        _suggest_nut_fix "$network" "$nut_host" "$nut_port"
        exit 1
    fi
}

_suggest_nut_fix() {
    local network="$1" host="$2" port="$3" gateway
    gateway="$(docker_network_gateway "$network" 2>/dev/null || true)"
    echo
    echo "  Proposte (NESSUNA viene eseguita automaticamente — NUT resta invariato):"
    echo "  - Se upsd ascolta già su un IP reale di questo server, impostare in .env:"
    echo "      NUT_HOST=<ip-reale-del-server>"
    if [[ -n "$gateway" ]]; then
        echo "  - Altrimenti, il gateway Docker rilevato ORA per la rete '${network}' è: ${gateway}"
        echo "    (NON è necessariamente 172.17.0.1: va riletto se la rete viene ricreata)"
        echo "      sudo cp /etc/nut/upsd.conf /etc/nut/upsd.conf.bak.\$(date +%F-%H%M%S)"
        echo "      sudo sed -i '\$a LISTEN ${gateway} ${port}' /etc/nut/upsd.conf"
        echo "      sudo systemctl reload nut-server   # reload, non restart"
    else
        warn "  Gateway della rete Docker non determinabile automaticamente in questo momento."
    fi
    echo "  Configurazione attuale: NUT_HOST=${host} NUT_PORT=${port}"
}

# ---------------------------------------------------------------- 5. build+migrate+verifica+up
step_compose() {
    echo
    echo "=== 5/8 — Build, migrazioni, avvio del Control Plane ==="

    if ! run docker compose --project-name "$PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" build; then
        error "Build delle immagini non riuscita. Nessun container è stato avviato: nulla da ripulire."
        exit 1
    fi

    echo
    info "Le migrazioni girano in un job dedicato, non all'avvio dell'app (§6.9: evita race sullo schema)."
    if ! run docker compose --project-name "$PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" \
        --profile migrate run --rm ups-migrate; then
        if [[ "$MODE" == "apply" ]]; then
            rollback_preserving_data "Il job di migrazione è fallito. BOOTSTRAP_ADMIN_PASSWORD NON è stato rimosso da .env: controllare 'docker compose logs ups-migrate'."
        fi
        exit 1
    fi

    if [[ "$MODE" == "apply" ]]; then
        local admin_email
        admin_email="$(env_get BOOTSTRAP_ADMIN_EMAIL)"
        info "Verifico che l'amministratore iniziale sia stato DAVVERO creato prima di toccare .env…"
        if ! admin_was_created "$admin_email"; then
            rollback_preserving_data "L'amministratore '${admin_email}' non risulta nel database dopo la migrazione. BOOTSTRAP_ADMIN_PASSWORD NON è stato rimosso: controllare i log di 'ups-migrate' prima di ripetere."
        fi
        info "Amministratore verificato nel database: rimuovo ora la password di bootstrap da .env."
        sed -i 's/^BOOTSTRAP_ADMIN_PASSWORD=.*/BOOTSTRAP_ADMIN_PASSWORD=/' "$ENV_FILE"
    fi

    echo
    if ! run docker compose --project-name "$PROJECT_NAME" --env-file "$ENV_FILE" -f "$COMPOSE_FILE" up -d; then
        if [[ "$MODE" == "apply" ]]; then
            rollback_preserving_data "Avvio dei container non riuscito."
        fi
        exit 1
    fi
}

# ---------------------------------------------------------------- 6. verifica stato effettivo
step_healthcheck() {
    echo
    echo "=== 6/8 — Verifica dello stato operativo effettivo ==="

    if [[ "$MODE" != "apply" ]]; then
        info "(saltata in anteprima: nulla è stato avviato)"
        return 0
    fi

    local bind_address web_port
    bind_address="$(env_get BIND_ADDRESS)"
    web_port="$(env_get WEB_PORT)"

    info "Attendo che il portale risponda (fino a 60s)…"
    local ok=0 _i
    for _i in $(seq 1 30); do
        if curl -skfS "https://${bind_address}:${web_port}/health/ready" >/dev/null 2>&1; then
            ok=1
            break
        fi
        sleep 2
    done

    if [[ "$ok" -ne 1 ]]; then
        rollback_preserving_data "Il portale non risponde dopo 60s. Controllare 'docker compose logs ups-web' e 'docker compose ps'."
    fi
    info "Portale raggiungibile su https://${bind_address}:${web_port}/"

    info "Verifico lo stato operativo effettivo nel database (non solo i log, §11 di questo script)…"
    if ! assert_dryrun_enforced; then
        rollback_preserving_data "Modalità effettiva non DryRun: deployment interrotto per sicurezza (vincolo inderogabile del progetto)."
    fi
    info "Confermato: ExecutionMode = DryRun nel database."

    local peanut_host
    peanut_host="$(peanut_check_host)"
    info "PeaNUT (NON toccato), verifica finale su ${peanut_host}:8085:"
    curl -s -o /dev/null -w '  -> %{http_code}\n' "http://${peanut_host}:8085/" 2>/dev/null || true
}

# ---------------------------------------------------------------- 7. safety agent
step_safety_agent() {
    echo
    echo "=== 7/8 — Safety Agent ==="

    if [[ "$MODE" != "apply" ]]; then
        info "In apply, questo passo chiamerebbe install-safety-agent.sh --apply, con una propria"
        info "conferma separata, usando sudo solo per questo passo e solo se non già root."
        return 0
    fi

    confirm "Installare ora il Safety Agent sull'host? Digitare 'installa' per confermare: " "installa"

    if [[ "$EUID" -eq 0 ]]; then
        "${SCRIPT_DIR}/install-safety-agent.sh" --apply
    elif command -v sudo >/dev/null 2>&1; then
        info "Non sono root: uso sudo solo per questo passo."
        sudo "${SCRIPT_DIR}/install-safety-agent.sh" --apply
    else
        error "Serve sudo (non trovato sul sistema) oppure eseguire questo script come root per installare il Safety Agent."
        error "Eseguire manualmente più tardi: sudo ${SCRIPT_DIR}/install-safety-agent.sh --apply"
        return 0
    fi

    echo
    warn "Compilare manualmente /etc/ups-control-center/safety-agent.env con gli stessi"
    warn "SAFETY_CONFIG_HMAC_KEY e SAFETY_AGENT_TOKEN presenti in ${ENV_FILE}, poi:"
    echo "    sudo systemctl restart ups-safety-agent"
}

# ---------------------------------------------------------------- 8. riepilogo
step_summary() {
    echo
    echo "=== 8/8 — Riepilogo e passi manuali rimasti ==="
    echo
    echo "NON eseguiti da questo script (richiedono la tua approvazione esplicita):"
    echo "  1. Firewall:"
    echo "       ${REPO_ROOT}/deploy/firewall/configure-firewall.sh          # anteprima"
    echo "       sudo ${REPO_ROOT}/deploy/firewall/configure-firewall.sh --apply"
    echo "  2. Se non già fatto: compilare safety-agent.env e riavviare il Safety Agent (passo 7)."
    echo "  3. Pubblicare una configurazione dal portale (Configurazioni → Anteprima → Pubblica):"
    echo "     il Safety Agent resta in SafeHold finché non lo fai."
    echo "  4. Login sul portale e cambio immediato della password iniziale."
    echo "  5. Backup automatici (docs/11 §11.9): profilo 'backup' di Docker Compose + cron per SQLite."
    echo
    echo "Verifica finale consigliata (docs/11 §11.8):"
    echo "  curl -k https://<BIND_ADDRESS>:<WEB_PORT>/health/ready"
    echo "  docker compose exec ups-db psql -U upscc -d upscc \\"
    echo "    -c \"SELECT key, value FROM ups.system_settings WHERE key IN ('ExecutionMode','LiveEnabledAdapters');\""
    echo "  # Deve restare: ExecutionMode = DryRun, LiveEnabledAdapters vuoto, finché non decidi tu di cambiarlo."
}

main() {
    echo "=============================================================="
    echo " UPS Control Center — installazione end-to-end"
    echo "=============================================================="
    if [[ "$MODE" == "dry-run" ]]; then
        warn "MODALITÀ ANTEPRIMA: nessuna modifica verrà applicata."
        warn "Per eseguire realmente: $0 --apply"
    else
        warn "MODALITÀ APPLICAZIONE: verranno fatte modifiche reali a .env, ai container e, se confermato, all'host."
        warn "NUT, PeaNUT, UFW e iptables non vengono mai toccati da questo script."
    fi

    step_system_preflight
    step_env
    step_resource_checks
    step_nut_connectivity

    if [[ "$MODE" == "apply" ]]; then
        confirm $'\nProcedere con build, migrazioni e avvio? Digitare \'deploy\' per confermare: ' "deploy"
    fi

    step_compose
    step_healthcheck
    step_safety_agent
    step_summary
}

# Permette di sorgere questo file nei test (deploy/scripts/test-install-all.sh) senza eseguire
# main automaticamente: con `source`, $0 resta il nome del chiamante, non di questo file.
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi
