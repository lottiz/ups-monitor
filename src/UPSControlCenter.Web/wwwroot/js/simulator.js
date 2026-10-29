/* Simulatore: invio scenario, timeline, esportazione (§14). */
(function () {
    'use strict';

    let timelineTable = null;

    function numberOrNull(id) {
        const el = document.getElementById(id);
        if (!el || el.value === '') { return null; }
        const parsed = Number(el.value);
        return Number.isFinite(parsed) ? parsed : null;
    }

    fetch('/Simulator/GetPresets')
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (presets) {
            const select = document.getElementById('preset');
            presets.forEach(function (p, index) {
                const option = document.createElement('option');
                option.value = String(index);
                option.textContent = p.name;
                select.appendChild(option);
            });

            select.addEventListener('change', function () {
                if (select.value === '') { return; }
                applyPreset(presets[Number(select.value)].scenario);
            });
        })
        .catch(function () {
            window.UpsCc.toast('Scenari predefiniti non disponibili.', 'warning');
        });

    function applyPreset(s) {
        function set(id, value) {
            const el = document.getElementById(id);
            if (el) { el.value = value === null || value === undefined ? '' : value; }
        }

        set('name', s.name);
        set('blackoutDuration', s.blackoutDurationSeconds);
        set('initialRuntime', s.initialRuntimeSeconds);
        set('initialCharge', s.initialBatteryChargePercent);
        set('decay', s.runtimeDecayPerSecond);
        set('lowBatteryAt', s.lowBatteryAtSecond);
        set('commLostFrom', s.communicationLostFromSecond);
        set('commLostTo', s.communicationLostToSecond);
        set('powerRestoredAt', s.powerRestoredAtSecond);
        set('secondBlackoutAt', s.secondBlackoutAtSecond);
        set('orchestratorCrashAt', s.orchestratorCrashAtSecond);
        set('tick', s.tickSeconds);
    }

    document.getElementById('runSimulation').addEventListener('click', async function () {
        const button = this;
        button.disabled = true;
        button.textContent = 'Simulazione in corso…';

        const scenario = {
            name: document.getElementById('name').value || 'Scenario personalizzato',
            blackoutDurationSeconds: numberOrNull('blackoutDuration') || 0,
            // null è significativo: simula un UPS che non fornisce l'autonomia (§9.3).
            initialRuntimeSeconds: numberOrNull('initialRuntime'),
            initialBatteryChargePercent: numberOrNull('initialCharge'),
            runtimeDecayPerSecond: numberOrNull('decay') || 1,
            lowBatteryAtSecond: numberOrNull('lowBatteryAt'),
            communicationLostFromSecond: numberOrNull('commLostFrom'),
            communicationLostToSecond: numberOrNull('commLostTo'),
            powerRestoredAtSecond: numberOrNull('powerRestoredAt'),
            secondBlackoutAtSecond: numberOrNull('secondBlackoutAt'),
            orchestratorCrashAtSecond: numberOrNull('orchestratorCrashAt'),
            tickSeconds: numberOrNull('tick') || 10
        };

        const result = await window.UpsCc.postJson('/Simulator/Run', scenario);

        button.disabled = false;
        button.textContent = 'Esegui simulazione';

        if (!result.ok) {
            const message = result.payload && result.payload.message
                ? result.payload.message
                : 'Simulazione non eseguita.';
            window.UpsCc.toast(message, 'danger');
            return;
        }

        render(result.payload);
    });

    function render(result) {
        const summary = document.getElementById('summary');
        const triggered = result.shutdownTriggered;

        summary.className = triggered ? 'alert alert-warning mb-0' : 'alert alert-success mb-0';
        summary.textContent = result.summary;

        const link = document.getElementById('exportLink');
        link.href = '/Simulator/Export?id=' + result.id;
        link.classList.remove('d-none');

        const columns = [
            { title: 'Secondo', field: 'simulatedSecond', width: 100, hozAlign: 'right' },
            {
                title: 'Durata', field: 'simulatedSecond', width: 110,
                formatter: function (cell) {
                    return window.UpsCc.formatDuration(cell.getValue());
                }
            },
            { title: 'Stato UPS', field: 'upsStatus', width: 140 },
            {
                title: 'Autonomia', field: 'remainingRuntimeSeconds', width: 120,
                formatter: function (cell) {
                    return window.UpsCc.formatDuration(cell.getValue());
                }
            },
            {
                title: 'Carica', field: 'batteryChargePercent', width: 100,
                formatter: function (cell) {
                    return window.UpsCc.formatNumber(cell.getValue(), '%');
                }
            },
            { title: 'Regola', field: 'appliedRule', width: 180 },
            { title: 'Azione prevista', field: 'plannedAction', width: 180 },
            { title: 'Motivazione', field: 'decisionReason', minWidth: 260 },
            { title: 'Nota', field: 'error', width: 200 }
        ];

        if (timelineTable) { timelineTable.destroy(); }

        timelineTable = new Tabulator('#timelineTable', {
            data: result.timeline,
            layout: 'fitColumns',
            pagination: true,
            paginationSize: 20,
            columns: columns,
            rowFormatter: function (row) {
                // Evidenzia la riga in cui la sequenza è scattata: è l'informazione che si cerca.
                if (row.getData().plannedAction === 'Avvio sequenza di spegnimento') {
                    row.getElement().classList.add('table-warning');
                }
            }
        });
    }
})();
