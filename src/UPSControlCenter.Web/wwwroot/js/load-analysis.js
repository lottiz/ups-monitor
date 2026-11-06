/* Analisi carico stanza per stanza: lettura live della corrente e registro delle letture
   annotate manualmente mentre si scollegano i dispositivi uno alla volta. */
(function () {
    'use strict';

    const upsId = document.querySelector('.page-heading[data-ups-id]').dataset.upsId;
    const storageKey = 'ups-load-analysis-' + upsId;

    const liveCurrentEl = document.getElementById('liveCurrent');
    const liveCurrentNoteEl = document.getElementById('liveCurrentNote');
    const livePowerEl = document.getElementById('livePower');
    const liveLoadEl = document.getElementById('liveLoad');
    const liveUpdatedAgoEl = document.getElementById('liveUpdatedAgo');
    const connDot = document.getElementById('liveConnDot');
    const connStateEl = document.getElementById('liveConnState');
    const markForm = document.getElementById('markForm');
    const markLabelInput = document.getElementById('markLabel');

    // Null finché non arriva una lettura: non si marca nulla basandosi su un valore inventato.
    let lastKnownCurrent = null;
    let lastKnownIsEstimate = false;
    let lastUpdateAt = null;

    function setConnState(text, state) {
        connStateEl.textContent = text;
        connDot.className = 'connection-dot connection-' + state;
    }

    // Il ciclo di diffusione lato server gira ogni 5s a prescindere da quando è arrivata
    // l'ultima lettura NUT: mostrare "quanto fa" rende chiaro se il dato è ancora vivo o se
    // il flusso si è fermato, invece di lasciare un numero statico che sembra sempre attuale.
    function tickUpdatedAgo() {
        if (lastUpdateAt === null) { return; }
        const seconds = Math.max(0, Math.round((Date.now() - lastUpdateAt) / 1000));
        liveUpdatedAgoEl.textContent = seconds <= 1 ? 'adesso' : seconds + ' secondi fa';
    }
    setInterval(tickUpdatedAgo, 1000);

    function loadEntries() {
        try {
            const raw = localStorage.getItem(storageKey);
            return raw ? JSON.parse(raw) : [];
        } catch (e) {
            return [];
        }
    }

    function saveEntries(entries) {
        localStorage.setItem(storageKey, JSON.stringify(entries));
    }

    const table = new Tabulator('#markTable', {
        data: loadEntries(),
        layout: 'fitColumns',
        placeholder: 'Nessuna lettura annotata. Lascia tutto collegato e premi "Segna lettura".',
        columns: [
            {
                title: 'Ora', field: 'time', width: 100,
                formatter: cell => window.UpsCc.formatTime(cell.getValue())
            },
            { title: 'Scollegato', field: 'label', minWidth: 200 },
            {
                title: 'Corrente', field: 'currentAmps', width: 130,
                formatter: cell => {
                    const row = cell.getRow().getData();
                    const value = window.UpsCc.formatNumber(cell.getValue(), 'A');
                    return row.isEstimate ? value + ' (stimata)' : value;
                }
            },
            {
                title: 'Differenza', field: 'deltaAmps', width: 130,
                formatter: cell => {
                    const value = cell.getValue();
                    if (value === null || value === undefined) { return '—'; }
                    return (value >= 0 ? '−' : '+') + window.UpsCc.formatNumber(Math.abs(value), 'A');
                }
            }
        ]
    });

    function updateLiveReadout(status) {
        if (!status) { return; }

        lastUpdateAt = Date.now();
        tickUpdatedAgo();

        if (status.outputCurrentAmps !== null && status.outputCurrentAmps !== undefined) {
            lastKnownCurrent = status.outputCurrentAmps;
            lastKnownIsEstimate = false;
            liveCurrentEl.textContent = window.UpsCc.formatNumber(status.outputCurrentAmps, 'A');
            liveCurrentNoteEl.textContent = 'Lettura diretta dal UPS';
        } else if (status.realPowerWatts !== null && status.realPowerWatts !== undefined
                   && status.outputVoltage) {
            // Nessuna corrente diretta dal driver: si stima da potenza e tensione (fattore di
            // potenza assunto 1), e la UI lo dichiara esplicitamente invece di spacciarla per
            // una misura reale (§9.3).
            const estimate = status.realPowerWatts / status.outputVoltage;
            lastKnownCurrent = estimate;
            lastKnownIsEstimate = true;
            liveCurrentEl.textContent = window.UpsCc.formatNumber(estimate, 'A');
            liveCurrentNoteEl.textContent = 'Stimata da potenza ÷ tensione, non da sensore diretto';
        } else {
            lastKnownCurrent = null;
            liveCurrentEl.textContent = 'non disponibile';
            liveCurrentNoteEl.textContent = 'Nessun dato di potenza o corrente disponibile';
        }

        livePowerEl.textContent = window.UpsCc.formatNumber(status.realPowerWatts, 'W');
        liveLoadEl.textContent = window.UpsCc.formatNumber(status.loadPercent, '%');
    }

    async function fetchInitialStatus() {
        try {
            const response = await fetch('/api/dashboard/ups');
            if (!response.ok) { return; }
            const list = await response.json();
            updateLiveReadout(list.find(u => u.id === upsId));
        } catch (e) {
            // Resta in attesa del primo aggiornamento via SignalR.
        }
    }

    fetchInitialStatus();

    if (typeof signalR !== 'undefined') {
        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/dashboard')
            .withAutomaticReconnect()
            .build();

        connection.on('UpsStatusChanged', function (status) {
            if (status.id === upsId) { updateLiveReadout(status); }
        });

        connection.onreconnecting(function () {
            setConnState('Riconnessione in corso…', 'wait');
        });
        connection.onreconnected(function () {
            setConnState('Connessione in tempo reale attiva', 'on');
        });
        connection.onclose(function () {
            setConnState('Connessione in tempo reale interrotta', 'off');
        });

        connection.start()
            .then(function () { setConnState('Connessione in tempo reale attiva', 'on'); })
            .catch(function () { setConnState('Connessione in tempo reale non riuscita', 'off'); });
    }

    markForm.addEventListener('submit', function (event) {
        event.preventDefault();

        if (lastKnownCurrent === null) {
            window.UpsCc.toast('Nessuna lettura di corrente disponibile al momento.', 'warning');
            return;
        }

        const entries = loadEntries();
        const previous = entries.length > 0 ? entries[entries.length - 1] : null;

        entries.push({
            time: new Date().toISOString(),
            label: markLabelInput.value.trim() || '(senza etichetta)',
            currentAmps: lastKnownCurrent,
            isEstimate: lastKnownIsEstimate,
            deltaAmps: previous ? previous.currentAmps - lastKnownCurrent : null
        });

        saveEntries(entries);
        table.setData(entries);
        markLabelInput.value = '';
        markLabelInput.focus();
    });

    document.getElementById('clearLog').addEventListener('click', function () {
        if (!confirm('Svuotare il registro delle letture per questo UPS?')) { return; }
        localStorage.removeItem(storageKey);
        table.setData([]);
    });

    document.getElementById('exportLog').addEventListener('click', function () {
        table.download('csv', 'analisi-carico.csv');
    });
})();
