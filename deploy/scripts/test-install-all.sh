#!/usr/bin/env bash
# shellcheck disable=SC2317
# (SC2317 "unreachable": falso positivo pervasivo in questo file — le funzioni finte sotto
#  sono invocate indirettamente, per nome, dalle funzioni sorgenti di install-all.sh)
#
# Test automatici per install-all.sh (principali percorsi di errore).
#
# Non serve Docker reale né un server NUT: i comandi esterni che install-all.sh invoca
# (docker, ss, systemctl, curl) sono sostituiti con funzioni bash finte definite qui, per
# isolare la logica dello script (validazione di .env, rilevamento conflitti, enforcement
# DryRun, verifica dell'amministratore, suggerimenti NUT) dal resto dell'infrastruttura.
#
# Uso: ./test-install-all.sh

set -uo pipefail  # NON -e: un'asserzione fallita non deve interrompere gli altri test

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TARGET="${SCRIPT_DIR}/install-all.sh"

PASS=0
FAIL=0

ok()  { PASS=$((PASS + 1)); printf '  \033[0;32mPASS\033[0m  %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  \033[0;31mFAIL\033[0m  %s\n' "$1"; }

assert_true() {
    local desc="$1"; shift
    if "$@" >/tmp/test-install-all.out 2>&1; then ok "$desc"; else bad "$desc (uscita=$?): $(cat /tmp/test-install-all.out)"; fi
}

assert_false() {
    local desc="$1"; shift
    if "$@" >/tmp/test-install-all.out 2>&1; then bad "$desc (doveva fallire ma non lo ha fatto)"; else ok "$desc"; fi
}

assert_eq() {
    local desc="$1" expected="$2" actual="$3"
    if [[ "$expected" == "$actual" ]]; then ok "$desc"; else bad "$desc (atteso='$expected' ottenuto='$actual')"; fi
}

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

write_valid_env() {
    cat > "$WORKDIR/.env" <<'EOF'
POSTGRES_DB=upscc
POSTGRES_USER=upscc
POSTGRES_PASSWORD=unapasswordmoltolunga123
IMAGE_REGISTRY=local
IMAGE_TAG=0.1.0
BIND_ADDRESS=10.0.0.8
WEB_PORT=8443
EXECUTION_MODE=DryRun
SAFETY_CONFIG_HMAC_KEY=MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWYwMTIzNDU2Nzg5YWJjZGVm
SAFETY_AGENT_TOKEN=dG9rZW5fZGlfcHJvdmFfYWJiYXN0YW56YV9sdW5nbw==
NUT_HOST=10.0.0.8
NUT_PORT=3493
NUT_UPS_NAME=riello
NUT_POLLING_SECONDS=10
HOST_STATE_DIR=/var/lib/ups-control-center
BACKUP_DIR=/var/backups/ups-control-center
BACKUP_RETENTION_DAYS=30
BOOTSTRAP_ADMIN_EMAIL=admin@example.com
BOOTSTRAP_ADMIN_PASSWORD=Str0ng!Passw0rd
TZ=Europe/Rome
EOF
}

set_env_value() {
    local key="$1" value="$2"
    sed -i "s|^${key}=.*|${key}=${value}|" "$WORKDIR/.env"
}

# shellcheck source=/dev/null
source "$TARGET"
# shellcheck disable=SC2034
# (usata dalle funzioni di install-all.sh appena sorgenti, non direttamente in questo file)
ENV_FILE="${WORKDIR}/.env"

echo "=== validate_env ==="

write_valid_env
assert_true "ambiente valido: validate_env ok" validate_env

write_valid_env; set_env_value EXECUTION_MODE Live
assert_false "EXECUTION_MODE=Live viene rifiutato (#3/#11)" validate_env

write_valid_env; set_env_value BIND_ADDRESS 0.0.0.0
assert_false "BIND_ADDRESS=0.0.0.0 viene rifiutato" validate_env

write_valid_env; set_env_value BIND_ADDRESS not-an-ip
assert_false "BIND_ADDRESS non valido viene rifiutato" validate_env

write_valid_env; set_env_value WEB_PORT 99999
assert_false "WEB_PORT fuori range viene rifiutato" validate_env

write_valid_env; set_env_value WEB_PORT abc
assert_false "WEB_PORT non numerico viene rifiutato" validate_env

write_valid_env; set_env_value POSTGRES_PASSWORD corta
assert_false "POSTGRES_PASSWORD troppo corta viene rifiutata" validate_env

write_valid_env; set_env_value SAFETY_CONFIG_HMAC_KEY dG9vc2hvcnQ=
assert_false "SAFETY_CONFIG_HMAC_KEY troppo corta (decodificata) viene rifiutata" validate_env

write_valid_env; set_env_value BOOTSTRAP_ADMIN_EMAIL non-una-email
assert_false "BOOTSTRAP_ADMIN_EMAIL malformata viene rifiutata" validate_env

write_valid_env; set_env_value BOOTSTRAP_ADMIN_PASSWORD sololetterequialunghe
assert_false "BOOTSTRAP_ADMIN_PASSWORD senza cifre/simboli viene rifiutata" validate_env

write_valid_env; set_env_value NUT_POLLING_SECONDS 0
assert_false "NUT_POLLING_SECONDS=0 viene rifiutato" validate_env

write_valid_env; set_env_value HOST_STATE_DIR relativo/non/assoluto
assert_false "HOST_STATE_DIR relativo viene rifiutato" validate_env

echo
echo "=== is_valid_port / is_valid_ipv4 ==="

assert_true  "porta 8443 valida" is_valid_port 8443
assert_false "porta 0 non valida" is_valid_port 0
assert_false "porta 65536 non valida" is_valid_port 65536
assert_false "porta non numerica" is_valid_port abc

assert_true  "10.0.0.8 è un IPv4 valido" is_valid_ipv4 10.0.0.8
assert_true  "0.0.0.0 è un IPv4 valido in formato (il divieto è una regola separata)" is_valid_ipv4 0.0.0.0
assert_false "999.1.1.1 non è un IPv4 valido" is_valid_ipv4 999.1.1.1
assert_false "host.docker.internal non è un IPv4" is_valid_ipv4 host.docker.internal

echo
echo "=== port_in_use (#8, ss mockato) ==="

ss() { printf 'State\tRecv-Q\tSend-Q\tLocal Address:Port\tPeer Address:Port\nLISTEN\t0\t128\t10.0.0.8:8443\t0.0.0.0:*\n'; }
assert_true  "10.0.0.8:8443 risulta occupata" port_in_use 10.0.0.8 8443
assert_false "10.0.0.8:9443 risulta libera" port_in_use 10.0.0.8 9443
unset -f ss

echo
echo "=== peanut_check_host (#6) ==="

write_valid_env
assert_eq "usa NUT_HOST (IP reale) come host PeaNUT" "10.0.0.8" "$(peanut_check_host)"

write_valid_env; set_env_value NUT_HOST host.docker.internal
assert_eq "con NUT_HOST=host.docker.internal ricade su 127.0.0.1" "127.0.0.1" "$(peanut_check_host)"

write_valid_env; { echo "PEANUT_HOST=192.0.2.1" >> "$WORKDIR/.env"; }
assert_eq "PEANUT_HOST esplicito ha sempre la precedenza" "192.0.2.1" "$(peanut_check_host)"

echo
echo "=== assert_dryrun_enforced (#3, db_query mockato) ==="

db_query() { echo "DryRun"; }
assert_true "ExecutionMode=DryRun viene accettato" assert_dryrun_enforced
unset -f db_query

db_query() { echo "Live"; }
assert_false "ExecutionMode=Live interrompe il deployment" assert_dryrun_enforced
unset -f db_query

db_query() { echo ""; }
assert_false "ExecutionMode irraggiungibile/vuoto non viene mai considerato sicuro" assert_dryrun_enforced
unset -f db_query

echo
echo "=== admin_was_created (#5, db_query mockato) ==="

db_query() { echo "1"; }
assert_true "amministratore trovato nel database" admin_was_created "admin@example.com"
unset -f db_query

db_query() { echo ""; }
assert_false "amministratore NON trovato: non si deve procedere" admin_was_created "admin@example.com"
unset -f db_query

echo
echo "=== compose_network_name / docker_network_gateway (#7, docker mockato) ==="

docker() {
    case "$1" in
        network)
            case "$2" in
                ls) echo "ups-control-center_ups-internal" ;;
                inspect) echo "192.168.200.1" ;;
            esac
            ;;
    esac
}
assert_eq "nome rete rilevato dalle etichette compose" "ups-control-center_ups-internal" \
    "$(compose_network_name ups-internal ups-control-center)"
assert_eq "gateway rilevato dinamicamente (non 172.17.0.1 fisso)" "192.168.200.1" \
    "$(docker_network_gateway ups-control-center_ups-internal)"
unset -f docker

echo
echo "=== nut_probe (docker mockato) ==="

docker() { [[ "$1" == "run" ]] && return 0; }
assert_true "nut_probe ha successo quando 'docker run' riesce" nut_probe fake-net 10.0.0.8 3493
unset -f docker

docker() { [[ "$1" == "run" ]] && return 1; }
assert_false "nut_probe fallisce quando 'docker run' fallisce" nut_probe fake-net 10.0.0.8 3493
unset -f docker

echo
echo "=============================================================="
printf "Risultato: %d passati, %d falliti\n" "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
