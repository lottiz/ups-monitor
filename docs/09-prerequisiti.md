# 9. Prerequisiti hardware e verifiche obbligatorie

## 9.1 Risorse del mini PC — da verificare, non presumere

Le risorse reali di `server-nut` non sono note. **Prima del deploy** eseguire sull'host:

```bash
nproc                                    # core disponibili
free -h                                  # RAM totale e disponibile
df -h / /var /opt                         # spazio libero
lsblk -o NAME,SIZE,ROTA,TYPE              # SSD o HDD (ROTA=0 ⇒ SSD)
docker info  | grep -Ei 'version|driver|cgroup'
docker compose version
lsb_release -a
uname -r
systemctl --version | head -1
psql --version 2>/dev/null || echo "psql client assente (non necessario: è in container)"
```

### Stima del fabbisogno di UPS Control Center

| Risorsa | Minimo | Raccomandato | Note |
|---|---|---|---|
| CPU | 2 core | 4 core | +PeaNUT e NUT già presenti |
| RAM | 3 GiB liberi | 6 GiB liberi | PostgreSQL 1 GiB, web 512 MiB, orchestrator 384 MiB, agent 128 MiB |
| Disco | 20 GiB liberi | 50 GiB | storico misure + backup + immagini Docker (~1.5 GiB) |
| Tipo disco | — | SSD/NVMe | `synchronous=FULL` su SQLite e fsync PostgreSQL penalizzano gli HDD |

Se le risorse rilevate sono inferiori al minimo: ridurre i limiti in `.env`, allungare
`polling_interval_seconds`, abbassare la retention delle misure, e **non** avviare il profilo
`simulation` in contemporanea al resto. In questi casi va riconsiderata la co-locazione con PeaNUT.

Crescita dello storico, misurata sul progetto: 1 UPS a 10s ⇒ ~8.640 righe/giorno ⇒ ~2,6 M/anno.
Con retention 90 giorni + aggregazione oraria lo spazio resta nell'ordine delle centinaia di MB.

## 9.2 Verifiche NUT (sola lettura, non distruttive)

```bash
upsc riello@127.0.0.1                     # elenco variabili effettive
upsc -l 127.0.0.1                          # UPS configurati
systemctl is-active nut-driver@riello nut-server
systemctl is-enabled nut-monitor           # atteso: disabled — NON cambiare
grep -v '^#' /etc/nut/upsd.conf | grep -i listen
grep -v '^#' /etc/nut/upsd.users | sed 's/password.*/password=***/'
```

L'output di `upsc` determina **quali campi la dashboard può mostrare**: le variabili assenti
restano vuote con etichetta "non disponibile". Non vengono mai mostrati valori calcolati o
inventati (§5.1).

Variabili attese dal driver `riello_ser` (da confermare sul campo): `ups.status`, `ups.load`,
`battery.charge`, `battery.runtime`, `battery.voltage`, `input.voltage`, `output.voltage`,
`input.frequency`, `output.frequency`, `ups.mfr`, `ups.model`, `ups.serial`.
I dati trifase (`input.L1-N.voltage`, …) sono presenti solo su alcune configurazioni: il modello
dati li gestisce come opzionali in `phase_data jsonb`.

## 9.3 Modifica richiesta ad `upsd.conf` — RICHIEDE APPROVAZIONE

Per permettere al container `ups-orchestrator` di leggere NUT serve che `upsd` ascolti sull'IP del
bridge Docker. Modifica **proposta, non applicata**:

```
# /etc/nut/upsd.conf  — aggiungere una riga, senza rimuovere le esistenti
LISTEN 172.17.0.1 3493
```

```bash
# comandi che verrebbero eseguiti (SOLO DOPO APPROVAZIONE)
sudo cp /etc/nut/upsd.conf /etc/nut/upsd.conf.bak.$(date +%F-%H%M%S)
sudo sed -i '$a LISTEN 172.17.0.1 3493' /etc/nut/upsd.conf
sudo systemctl reload nut-server   # reload, non restart: non interrompe il driver
upsc riello@172.17.0.1             # verifica
```

Rollback: ripristino del `.bak` e `reload`.

**Alternativa senza toccare NUT**: `NUT_ACCESS_MODE=viaAgent` — il Safety Agent (che legge NUT su
loopback) espone le misure su `127.0.0.1` e l'orchestrator le consuma. Leggermente più accoppiato,
ma a impatto zero sull'host. È il default di Fase 1 proprio per non richiedere modifiche.

## 9.4 Firewall UFW e interazione con Docker

Problema noto: Docker scrive direttamente in `iptables` (catena `DOCKER`) **bypassando UFW**. Una
porta pubblicata con `ports: - "8443:8443"` diventa raggiungibile da tutte le reti anche se UFW la
nega. Mitigazioni adottate, in combinazione:

1. **Binding esplicito all'IP**: `ports: - "10.0.0.8:8443:8443"`. Non pubblica su `0.0.0.0`.
2. **Regole in `DOCKER-USER`**, la catena che Docker rispetta:

```bash
# consenti solo le reti aziendali verso la porta del portale
for net in 10.0.0.0/24 172.16.10.0/24 192.168.98.0/24 192.168.114.0/24; do
  sudo iptables -I DOCKER-USER -p tcp --dport 8443 -s "$net" -j RETURN
done
sudo iptables -A DOCKER-USER -p tcp --dport 8443 -j DROP
```

3. **Filtro IP applicativo** (§8.5) come terza barriera, indipendente dalla rete.
4. UFW per le porte non-Docker (SSH, 3493 host):

```bash
sudo ufw status numbered                 # verificare PRIMA
# proposte, da approvare:
sudo ufw allow from 10.0.0.0/24 to any port 3493 proto tcp
```

Le regole `iptables` non sono persistenti di default: va installato `iptables-persistent` oppure
usata una unit systemd dedicata. Lo script `deploy/firewall/ufw-rules.sh` **stampa** le regole e
chiede conferma, non le applica da solo.

Da non fare: **non toccare** le regole esistenti per la porta 8085 (PeaNUT).

## 9.5 Prerequisiti sui dispositivi gestiti (Fase 3-4, da verificare caso per caso)

| Tipo | Spegnimento | Accensione | Verifica richiesta |
|---|---|---|---|
| Windows Server | WinRM HTTPS 5986 + `SeShutdownPrivilege` | WoL o BMC | `Test-WSMan`, certificato valido |
| Ubuntu/Linux | SSH chiave + sudoers limitato | WoL (`ethtool -s eth0 wol g`) | WoL spesso **disabilitato** di default e perso al cold boot: verificare nel BIOS |
| Docker Host | agente remoto, arresto stack ordinato | WoL / BMC | nessun socket esposto |
| NAS QNAP | SSH o API ufficiale | WoL se supportato dal modello | verificare il supporto reale sul modello specifico |
| Lenovo + XCC2 | Redfish `GracefulShutdown` | Redfish `On` | credenziali XCC dedicate, certificato; **mai** `ForceOff` automatico |
| Mini PC NUT | ultimo, disabilitato per default | **BIOS: Restore on AC Power Loss = Power On** | verificare la voce nel BIOS specifico |
| Switch / rete | — | alimentazione da UPS + auto-power-on | senza rete niente WoL né Redfish |

**Punto critico di §12**: la riaccensione automatica funziona solo se gli apparati di rete tornano
su da soli. Vanno quindi alimentati dall'UPS e configurati per l'accensione automatica; altrimenti
la sequenza di ripristino non ha un percorso di rete per raggiungere i dispositivi.
Le capability reali non sono assunte: ogni dispositivo ha un campo di capability verificato con
test non distruttivo e, finché non verificato, resta `Unknown` e non viene usato per l'accensione.
