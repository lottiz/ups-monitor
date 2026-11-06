# 11. Manuale di installazione

Destinatario: amministratore di sistema con accesso `sudo` su `server-nut` (10.0.0.8).

> Nessun passo di questa procedura modifica NUT o PeaNUT. I tre interventi sull'host che
> **richiedono approvazione** sono isolati nelle sezioni 11.3, 11.5 e 11.7. Non eseguire
> i comandi marcati come modifica dell'host senza aver prima verificato l'anteprima.

## 11.1 Verifiche preliminari (nessuna modifica)

Eseguire sull'host e **conservare l'output**: determina i valori di configurazione.

```bash
# Risorse reali: non vanno presunte (§9.1)
nproc
free -h
df -h / /var /opt
lsblk -o NAME,SIZE,ROTA,TYPE

# Versioni installate
. /etc/os-release && echo "$PRETTY_NAME"
uname -r
docker --version && docker compose version
systemctl --version | head -1

# NUT: stato dei servizi (da NON modificare)
systemctl is-active nut-driver@riello nut-server
systemctl is-enabled nut-monitor          # atteso: disabled

# Variabili effettivamente disponibili sull'UPS: determina cosa la dashboard può mostrare
upsc riello@127.0.0.1

# PeaNUT: deve restare funzionante
docker ps --filter name=nut-peanut
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8085/
```

### Soglie minime

| Risorsa | Minimo | Se inferiore |
|---|---|---|
| CPU | 2 core | aumentare `NUT_POLLING_SECONDS`, non avviare il profilo `simulation` |
| RAM libera | 3 GiB | ridurre `DB_MEM_LIMIT` e `WEB_MEM_LIMIT` in `.env` |
| Disco libero | 20 GiB | ridurre `MeasurementRetentionDays` dalle impostazioni |

Lo script `./deploy/scripts/install-safety-agent.sh` (senza argomenti) esegue
automaticamente queste verifiche in sola lettura e ne stampa un riepilogo.

## 11.2 Preparazione dei segreti

```bash
cd /opt/ups-control-center        # o la directory scelta per il repository
cp .env.example .env
chmod 600 .env

# Generazione dei tre segreti obbligatori
echo "POSTGRES_PASSWORD=$(openssl rand -base64 36)"
echo "SAFETY_CONFIG_HMAC_KEY=$(openssl rand -base64 48)"
echo "SAFETY_AGENT_TOKEN=$(openssl rand -base64 32)"
```

Compilare `.env` con questi valori più:

```ini
BIND_ADDRESS=10.0.0.8
WEB_PORT=8443
EXECUTION_MODE=DryRun          # non modificare
NUT_HOST=host.docker.internal  # oppure 10.0.0.8, vedi 11.3
BOOTSTRAP_ADMIN_EMAIL=amministratore@azienda.it
BOOTSTRAP_ADMIN_PASSWORD=<almeno 12 caratteri, maiuscole, cifre, simboli>
```

`SAFETY_CONFIG_HMAC_KEY` e `SAFETY_AGENT_TOKEN` andranno riportati **identici** nella
configurazione del Safety Agent (11.6): sono il canale di fiducia fra i due piani.

## 11.3 Accesso a NUT dai container — scelta richiesta

NUT gira sull'host, i container hanno una rete propria. Due opzioni:

### Opzione A — nessuna modifica all'host (consigliata per il primo deploy)

Se `upsd` ascolta già su `10.0.0.8`, impostare in `.env`:

```ini
NUT_HOST=10.0.0.8
```

Verifica: `upsc riello@10.0.0.8` deve rispondere.

### Opzione B — una riga aggiuntiva in `upsd.conf` (richiede approvazione)

Serve se `upsd` ascolta solo su `127.0.0.1`. **Modifica all'host:**

```bash
# Backup prima di qualunque modifica
sudo cp /etc/nut/upsd.conf /etc/nut/upsd.conf.bak.$(date +%F-%H%M%S)

# Aggiunge una riga LISTEN, senza rimuovere quelle esistenti
sudo sed -i '$a LISTEN 172.17.0.1 3493' /etc/nut/upsd.conf

# reload, NON restart: non interrompe il driver né le sessioni di PeaNUT
sudo systemctl reload nut-server

# Verifica
upsc riello@172.17.0.1
```

Rollback: ripristinare il `.bak` e ripetere il `reload`.

## 11.4 Build e migrazioni

```bash
docker compose build

# Le migrazioni girano in un job dedicato e non all'avvio dell'app: due istanze che
# migrano in parallelo produrrebbero una race sullo schema (§6.9).
docker compose --profile migrate run --rm ups-migrate
```

L'output deve riportare la creazione dei ruoli, dell'amministratore iniziale, dell'UPS
predefinito e della policy «Policy 30+30» in modalità DRY_RUN.

Subito dopo, **rimuovere le credenziali di bootstrap** da `.env`:

```bash
sed -i 's/^BOOTSTRAP_ADMIN_PASSWORD=.*/BOOTSTRAP_ADMIN_PASSWORD=/' .env
```

## 11.5 Avvio del Control Plane

```bash
docker compose up -d
docker compose ps

# Health check: deve riportare Healthy
curl -kfsS https://10.0.0.8:8443/health/ready

# PeaNUT deve continuare a funzionare
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8085/
```

Verificare nei log che la modalità effettiva sia DRY_RUN:

```bash
docker compose logs ups-web | grep -i "modalità effettiva"
docker compose logs ups-orchestrator | grep -i "modalità effettiva"
```

### Fiducia nella CA interna di Caddy — modifica dell'host, richiede approvazione

Caddy emette il certificato per l'IP configurato in `BIND_ADDRESS`. Prima di installare la sua
CA sull'host, estrarla e ispezionarla senza apportare modifiche:

```bash
mkdir -p /tmp/ups-caddy-ca-preview
docker compose cp ups-proxy:/data/caddy/pki/authorities/local/root.crt \
  /tmp/ups-caddy-ca-preview/ups-control-center-caddy.crt
openssl x509 -in /tmp/ups-caddy-ca-preview/ups-control-center-caddy.crt \
  -noout -subject -issuer -fingerprint -sha256
```

Dopo l'approvazione, rendere attendibile la CA per i client di sistema, incluso il Safety Agent:

```bash
sudo install -m 0644 /tmp/ups-caddy-ca-preview/ups-control-center-caddy.crt \
  /usr/local/share/ca-certificates/ups-control-center-caddy.crt
sudo update-ca-certificates

# Ora la verifica deve riuscire senza disabilitare la validazione TLS.
curl -fsS https://10.0.0.8:8443/health/ready
```

I browser sugli altri client LAN devono importare la stessa CA nel proprio trust store. Il
volume `ups-caddy-data` va conservato: se viene ricreato, Caddy genera una CA diversa.

## 11.6 Installazione del Safety Agent

```bash
# Anteprima: mostra tutti i comandi senza eseguirli
./deploy/scripts/install-safety-agent.sh

# Applicazione: richiede di digitare 'installa' per confermare
sudo ./deploy/scripts/install-safety-agent.sh --apply
```

Lo script crea l'utente `upsagent`, le directory di stato, copia i binari
self-contained, installa la unit systemd e avvia il servizio.

Compilare poi il file dei segreti:

```bash
sudo nano /etc/ups-control-center/safety-agent.env
```

Inserire **gli stessi valori** di `.env`:

```ini
SafetyAgent__ConfigHmacKeyBase64=<identico a SAFETY_CONFIG_HMAC_KEY>
SafetyAgent__AgentToken=<identico a SAFETY_AGENT_TOKEN>
SafetyAgent__ControlPlaneBaseUrl=https://10.0.0.8:8443
```

```bash
sudo systemctl restart ups-safety-agent
sudo systemctl status ups-safety-agent
sudo journalctl -u ups-safety-agent -f
```

### Stato atteso al primo avvio: `SafeHold`

L'agent parte in `SafeHold` perché nessuna configurazione è ancora stata pubblicata:
**monitora e registra, ma non esegue azioni**. È il comportamento corretto — senza una
configurazione firmata l'ordine delle dipendenze non è conoscibile e agire sarebbe più
pericoloso del non agire (§7.2).

Per uscire da `SafeHold`, pubblicare la configurazione dal portale:
**Configurazioni → Anteprima → Pubblica**. L'agent la scarica entro 60 secondi, verifica
checksum e firma HMAC, la valida e la attiva. Lo stato passa a `Monitoring`.

## 11.7 Firewall — richiede approvazione

Docker scrive direttamente in `iptables` scavalcando UFW: una porta pubblicata resta
raggiungibile anche se UFW la nega (R9). Servono tre barriere.

```bash
# Anteprima delle regole, senza applicarle
./deploy/firewall/configure-firewall.sh

# Applicazione (mantenere una sessione SSH aperta durante l'operazione)
sudo ./deploy/firewall/configure-firewall.sh --apply

# Le regole iptables NON sopravvivono al riavvio: renderle persistenti
sudo apt-get install -y iptables-persistent
sudo netfilter-persistent save
```

Lo script non tocca le regole relative alla porta 8085 di PeaNUT.

## 11.8 Verifica finale

```bash
# 1. Il portale risponde dalla rete autorizzata
curl -k https://10.0.0.8:8443/health/ready

# 2. L'orchestrator sta leggendo NUT: le misure crescono
docker compose exec ups-db psql -U upscc -d upscc \
  -c "SELECT count(*), max(recorded_at) FROM ups.ups_measurements;"

# 3. La modalità è DRY_RUN
docker compose exec ups-db psql -U upscc -d upscc \
  -c "SELECT key, value FROM ups.system_settings WHERE key IN ('ExecutionMode','LiveEnabledAdapters');"

# 4. Il Safety Agent è attivo e ha una configurazione
sudo systemctl is-active ups-safety-agent
sudo sqlite3 /var/lib/ups-control-center/safety.db \
  "SELECT version, state, activated_at FROM safety_config WHERE state='Active';"

# 5. PostgreSQL NON è raggiungibile dall'esterno
ss -tlnp | grep 5432 || echo "corretto: la 5432 non è pubblicata"

# 6. PeaNUT è intatto
curl -s -o /dev/null -w '%{http_code}\n' http://10.0.0.8:8085/
```

Accedere poi a `https://10.0.0.8:8443` con le credenziali dell'amministratore iniziale e
**cambiare immediatamente la password**.

## 11.9 Backup automatici

```bash
# Backup PostgreSQL con retention e verifica di ripristinabilità
docker compose --profile backup up -d ups-db-backup

# Backup SQLite del Safety Agent, da pianificare con cron
sudo crontab -e
```

```cron
# Backup giornaliero del database del Safety Agent (VACUUM INTO: consistente)
30 2 * * * /opt/ups-control-center/deploy/scripts/backup-sqlite.sh >> /var/log/ups-backup.log 2>&1
```

**Il volume `ups-dataprotection` va incluso nei backup**: contiene le chiavi di cifratura
delle credenziali. Perderlo le rende illeggibili in modo definitivo (R12).

```bash
docker run --rm -v ups-dataprotection:/keys:ro \
  -v /var/backups/ups-control-center:/backup \
  busybox tar czf /backup/dataprotection-$(date +%F).tar.gz -C /keys .
```

## 11.10 Disinstallazione

```bash
sudo systemctl disable --now ups-safety-agent
sudo rm /etc/systemd/system/ups-safety-agent.service
sudo systemctl daemon-reload

docker compose down            # senza -v: i volumi e i dati restano

# Rimozione dei dati (irreversibile), solo se richiesto:
#   docker volume rm ups-db-data ups-dataprotection
#   sudo rm -rf /var/lib/ups-control-center /opt/ups-control-center
```

NUT e PeaNUT restano invariati: il sistema non ha mai modificato la loro configurazione.
