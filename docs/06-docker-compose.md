# 6. Piano Docker Compose

## 6.1 Servizi e immagini (versioni fissate)

| Servizio | Immagine | Porte | Restart | Limiti |
|---|---|---|---|---|
| `ups-db` | `postgres:17.6-bookworm` | **nessuna pubblicata** | `unless-stopped` | 1 CPU / 1 GiB |
| `ups-web` | build locale, `mcr.microsoft.com/dotnet/aspnet:10.0` | nessuna (HTTP 8080 solo sulle reti Docker) | `unless-stopped` | 1 CPU / 512 MiB |
| `ups-proxy` | `caddy:2-alpine` | `10.0.0.8:8443:443` | `unless-stopped` | 0.25 CPU / 128 MiB |
| `ups-orchestrator` | build locale, runtime `dotnet/aspnet:10.0` | nessuna | `unless-stopped` | 0.5 CPU / 384 MiB |
| `ups-notifications` | stessa immagine orchestrator, entrypoint diverso | nessuna | `unless-stopped` | 0.25 CPU / 256 MiB |
| `ups-simulator` | stessa immagine orchestrator | nessuna | `no` (profilo `simulation`) | 0.5 CPU / 384 MiB |
| `ups-db-backup` | `postgres:17.6-bookworm` + cron | nessuna | `unless-stopped` | 0.25 CPU / 256 MiB |

I limiti sono volutamente conservativi: il mini PC ospita già PeaNUT, NUT e il Safety Agent, e le
sue risorse reali non sono note (§25.3) — vanno verificate con `nproc` / `free -h` prima del deploy
e i valori in `.env` adeguati.

Il portale conserva gli istanti in UTC e li converte per la sola visualizzazione nel fuso indicato
da `TZ` (default `Europe/Rome`). La conversione usa le regole IANA incluse nell'immagine Web, quindi
il passaggio tra ora solare e ora legale è automatico e non dipende dal fuso locale del container o
del browser.

## 6.2 Reti

```yaml
networks:
  ups-internal:   # bridge interno: db, orchestrator, notifications, web
    internal: true
  ups-edge:       # solo web, per l'esposizione LAN
```

`ups-db` è collegato **esclusivamente** a `ups-internal` (`internal: true` ⇒ nessun gateway verso
l'esterno). La porta 5432 non è mai pubblicata: §19.

## 6.3 Accesso a NUT dall'orchestrator

NUT gira sull'host, non in Docker. Opzioni valutate:

1. ~~`network_mode: host`~~ — scartata: annulla l'isolamento di rete.
2. **`extra_hosts: ["host.docker.internal:host-gateway"]`** — *scelta*. L'orchestrator si connette a
   `host.docker.internal:3493`. Richiede che `upsd` sia in `LISTEN` sull'IP del bridge Docker
   (`172.17.0.1`) oppure su `10.0.0.8`. **Questa è una modifica a `upsd.conf` e richiede
   approvazione esplicita** (§25.20) — vedi `docs/09-prerequisiti.md`.
3. Fallback senza toccare NUT: l'orchestrator legge le misure dal Safety Agent via API locale.
   Disponibile come `NUT_ACCESS_MODE=viaAgent`.

## 6.4 Health checks

- `ups-db`: `pg_isready -U $POSTGRES_USER -d $POSTGRES_DB` — interval 10s, retries 5, start_period 30s.
- `ups-web`: `curl -fsS http://localhost:8080/health/ready` — endpoint ASP.NET Core HealthChecks che
  verifica PostgreSQL, migrazioni applicate, Data Protection, heartbeat orchestrator.
- `ups-orchestrator`: health check via file sentinella (`/tmp/healthy`, aggiornato dal loop di
  polling) e confronto della sua età — non espone porte, quindi non c'è endpoint HTTP da sondare.
  L'immagine è `aspnet` e non `runtime` perché `UPSControlCenter.Infrastructure` usa Data
  Protection, che vive nel framework condiviso ASP.NET Core.
- `depends_on` con `condition: service_healthy` per `ups-web` e `ups-orchestrator` verso `ups-db`.

## 6.5 Volumi

| Volume | Contenuto | Backup |
|---|---|---|
| `ups-db-data` | PGDATA | `pg_dump` logico + `pg_basebackup` opzionale |
| `ups-dataprotection` | chiavi Data Protection (cifratura credenziali) | **obbligatorio e critico** |
| `ups-caddy-data` | CA interna e certificati TLS di Caddy | sì, per non cambiare CA al ripristino |
| `ups-backups` → bind `/var/backups/ups-control-center` | dump e dump SQLite | fuori da Docker |
| `ups-logs` | log strutturati JSON | rotazione |

Perdere `ups-dataprotection` rende illeggibili tutte le credenziali salvate: il backup di questo
volume è documentato come passo non opzionale.

## 6.6 Segreti

- Nessun segreto nel `docker-compose.yml` né nel repository.
- `.env` (permessi `0600`, non versionato) con: `POSTGRES_PASSWORD`, `SAFETY_CONFIG_HMAC_KEY`,
  `SMTP_PASSWORD`, `ASPNETCORE_Kestrel__Certificates__Default__Password`.
- `.env.example` contiene solo placeholder e il comando per generare valori
  (`openssl rand -base64 48`).
- Prospettiva: migrazione a `docker secret` se si introduce Swarm; oggi non giustificato.

## 6.7 Sicurezza dei container

Per ogni servizio:

```yaml
security_opt: ["no-new-privileges:true"]
cap_drop: ["ALL"]
read_only: true                 # + tmpfs per /tmp
user: "64198:64198"             # non root
```

Nessun container privilegiato, nessun mount del Docker socket (§15). L'arresto degli host Docker
remoti (§8.3) avviene tramite agente remoto a privilegi limitati, non esponendo socket.

## 6.8 Logging

`driver: json-file`, `max-size: 10m`, `max-file: 5` per servizio. Log applicativi in JSON
strutturato con `Serilog`; filtro di redazione che azzera campi `password`, `secret`, `token`,
`ciphertext` prima della scrittura (§15 "nessuna password nei log").

## 6.9 Aggiornamento e rollback

Aggiornamento:
1. `./deploy/scripts/backup-postgres.sh` + backup SQLite + tag immagini correnti.
2. `docker compose build --pull`
3. `docker compose run --rm ups-web migrate` (migrazioni EF applicate in un job dedicato, non
   all'avvio dell'app, per evitare race tra più istanze).
4. `docker compose up -d` con health check verificati.

Rollback:
1. `docker compose down` (non `-v`: i volumi restano).
2. Ripristino tag immagine precedente in `.env` (`IMAGE_TAG`).
3. Se la migrazione ha cambiato lo schema: `restore-postgres.sh <dump>` — le migrazioni EF sono
   generate con script di `Down` verificato e il dump pre-aggiornamento è il percorso primario.
4. Il Safety Agent, non containerizzato, continua a funzionare durante tutta la procedura con la
   sua ultima configurazione valida.

## 6.10 Vincoli di non-regressione

- PeaNUT (`nut-peanut`, porta 8085, `/opt/nut-dashboard`) non viene toccato: nessun servizio
  dichiarato in questo compose, nessuna porta in conflitto, nessun nome container sovrapposto.
- Nessun secondo NUT installato: non esistono immagini NUT nel compose.
- Nomi container prefissati `ups-` e `container_name` espliciti per evitare collisioni.
