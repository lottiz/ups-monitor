# 8. Piano sicurezza

## 8.1 DRY_RUN come confine di composizione

Il requisito "in DRY_RUN nessun comando reale" non è implementato con controlli sparsi, ma così:

```csharp
// Infrastructure/Adapters/AdapterRegistration.cs
public static IServiceCollection AddDeviceAdapters(this IServiceCollection s, ExecutionModeOptions o)
{
    s.AddSingleton<IDeviceAdapter, DryRunDeviceAdapter>();   // sempre presente

    if (o.Mode != ExecutionMode.Live)
        return s;                                            // ← nessun adapter reale registrato

    foreach (var name in o.LiveEnabledAdapters)               // abilitazione PER ADAPTER
        s.AddRealAdapter(name);
    return s;
}
```

`IAdapterSelector` risolve l'adapter per dispositivo; se la modalità non è `Live` restituisce
**sempre** il `DryRunDeviceAdapter`, e non esiste percorso di codice che istanzi un adapter reale
senza passare da qui (costruttori `internal`, nessun `new` pubblico).

Test obbligatori (§21):
- in DRY_RUN, `provider.GetServices<IDeviceAdapter>()` contiene solo il tipo dry-run;
- nessun tipo reale è assegnabile da `IAdapterSelector` in DRY_RUN;
- gli adapter reali non effettuano I/O di rete nei test (socket factory iniettata e asserita non usata).

## 8.2 Abilitazione LIVE — procedura a più fattori

Nessun singolo pulsante. Perché un'azione reale avvenga servono **tutte** queste condizioni:

1. `SystemSettings.ExecutionMode = Live` — modificabile solo da `Administrator`.
2. L'adapter specifico in `LiveEnabledAdapters` — abilitazione separata per Ssh, WinRm, Redfish,
   Qnap, WakeOnLan, ciascuna con la propria approvazione e voce di audit.
3. Il dispositivo con `is_enabled = true` e un test di connessione non distruttivo superato di recente.
4. Conferma esplicita nell'UI: digitazione del nome del dispositivo/adapter + motivazione testuale.
5. Variabile d'ambiente di deployment `UPS_ALLOW_LIVE=1` sul container/servizio: senza di essa la
   configurazione LIVE nel database viene **ignorata** e loggata come rifiutata. Questo impedisce
   che un ripristino di database da un ambiente diverso attivi azioni reali.

Ogni passaggio produce `AuditLog` con utente, IP, prima/dopo, motivazione.

## 8.3 Kill switch

`deploy/scripts/disable-automation.sh` e l'endpoint `POST /api/admin/automation/disable`
(solo Administrator, rate-limited):

- imposta `ExecutionMode = DryRun` e svuota `LiveEnabledAdapters` in PostgreSQL;
- scrive il file sentinella `/var/lib/ups-control-center/AUTOMATION_DISABLED`, che il Safety Agent
  controlla a ogni ciclo **prima** di qualsiasi valutazione: se presente, nessuna azione;
- **il monitoraggio, lo storico e le notifiche restano attivi** (§15);
- funziona anche con il portale giù, perché il file sentinella è locale all'host.

Lo script è l'unico percorso che non richiede il portale, per questo è il meccanismo di emergenza
primario.

## 8.4 Credenziali

- Cifratura con **ASP.NET Core Data Protection**, chiavi su volume dedicato `ups-dataprotection`,
  protette a riposo; purpose string distinta per tipo di segreto.
- Le credenziali non sono mai restituite dalle API: i DTO espongono solo `HasSecret`,
  `Fingerprint`, `UpdatedAt`. Nessun endpoint di lettura del segreto in chiaro.
- Serilog con `Destructurers` e un enricher di redazione che annulla proprietà il cui nome
  corrisponde a `password|secret|token|key|credential|ciphertext` (case-insensitive).
- Chiavi SSH: supportata solo autenticazione a chiave con passphrase; host key verificata contro
  fingerprint memorizzato (no `StrictHostKeyChecking=no`).

## 8.5 Superficie web

- HTTPS obbligatorio, HSTS, redirect da HTTP.
- Antiforgery token su ogni POST/PUT/DELETE MVC; le API usano cookie+antiforgery o bearer.
- Rate limiting (`Microsoft.AspNetCore.RateLimiting`): limiti stretti su login (5/min/IP),
  operazioni sensibili (3/min/utente), API generiche (100/min).
- CSP restrittiva, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options: DENY`.
- Filtro IP middleware: solo `10.0.0.0/24`, `172.16.10.0/24`, `192.168.98.0/24`, `192.168.114.0/24`
  (+ loopback per l'agent). Configurabile, con `ForwardedHeaders` gestito in modo esplicito e
  `KnownProxies` dichiarati, per evitare spoofing di `X-Forwarded-For`.
- Cookie `Secure`, `HttpOnly`, `SameSite=Strict`; sessione con scadenza e lockout Identity.

## 8.6 RBAC

| Capability | Viewer | Operator | Administrator |
|---|---|---|---|
| Vedere dashboard, storico, eventi | ✔ | ✔ | ✔ |
| Eseguire simulazioni | — | ✔ | ✔ |
| CRUD dispositivi e gruppi | — | ✔ | ✔ |
| Test connessione non distruttivo | — | ✔ | ✔ |
| Modificare regole/sequenze (Draft) | — | ✔ | ✔ |
| Pubblicare configurazione | — | — | ✔ |
| Gestire credenziali | — | — | ✔ |
| Abilitare LIVE / singolo adapter | — | — | ✔ |
| Kill switch | — | ✔ | ✔ |
| Utenti e ruoli, impostazioni | — | — | ✔ |

Autorizzazione con policy nominate (`Policies.PublishConfiguration`, `Policies.EnableLive`, …),
non con controlli di ruolo sparsi nelle view.

## 8.7 Divieti espliciti implementati

- Nessun container privilegiato, `cap_drop: ALL`, `no-new-privileges` (§6.7).
- Nessun Docker socket montato nel portale.
- Nessun comando shell arbitrario: non esiste API che accetti una stringa di comando. Gli step
  `Script*` sono rifiutati dal validatore di configurazione.
- Nessuna credenziale NUT di controllo (`upsmon` master) nella dashboard: il portale usa
  **solo accesso di lettura** a NUT.
- Nessun `FSD`, nessun `killpower`, nessun `upsdrvctl shutdown`: questi comandi non compaiono in
  nessun punto del codice. Un test grep-based del repository lo verifica.
- `nut-monitor` resta disabilitato; il codice non lo abilita né scrive in `/etc/nut`.

## 8.8 Minimo privilegio sui dispositivi gestiti

Account dedicati per adapter, documentati, con i soli diritti necessari:
Linux — `sudo` limitato a `/sbin/shutdown` via `sudoers.d` con `NOPASSWD` sul solo comando;
Windows — diritto `SeShutdownPrivilege` e WinRM con endpoint ristretto;
Redfish — ruolo `Operator` di XCC, non `Administrator`;
QNAP — utente dedicato con permesso di arresto.
Nessun account amministrativo di dominio.
