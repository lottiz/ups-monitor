/* Dashboard: aggiornamenti in tempo reale via SignalR e tabella eventi. */
(function () {
    'use strict';

    const eventsTable = new Tabulator('#eventsTable', {
        ajaxURL: '/api/dashboard/events?limit=200',
        layout: 'fitColumns',
        pagination: true,
        paginationSize: 15,
        placeholder: 'Nessun evento registrato.',
        columns: [
            {
                title: 'Data e ora', field: 'occurredAt', width: 180,
                formatter: function (cell) {
                    return new Date(cell.getValue()).toLocaleString('it-IT');
                }
            },
            { title: 'Tipo', field: 'eventType', headerFilter: 'input' },
            {
                title: 'Gravità', field: 'severity', width: 130, headerFilter: 'list',
                headerFilterParams: { values: ['Information', 'Warning', 'Critical'] },
                formatter: function (cell) {
                    const v = cell.getValue();
                    const cls = v === 'Critical' ? 'danger' : (v === 'Warning' ? 'warning' : 'secondary');
                    return '<span class="badge text-bg-' + cls + '">' + v + '</span>';
                }
            },
            { title: 'Stato UPS', field: 'newStatus', width: 140 },
            { title: 'Blackout', field: 'blackoutId', headerFilter: 'input' },
            { title: 'Rilevato da', field: 'detectedBy', width: 140 }
        ]
    });

    // Ricarica periodica: copre anche il caso in cui SignalR non sia disponibile.
    setInterval(function () { eventsTable.replaceData(); }, 30000);

    const stateLabel = document.getElementById('connectionState');

    const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/dashboard')
        .withAutomaticReconnect()
        .build();

    function setText(id, text) {
        const el = document.getElementById(id);
        if (el) { el.textContent = text; }
    }

    connection.on('AutomationStatusChanged', function (automation) {
        window.UpsCc.updateModeBadge(automation);

        setText('autoMode', automation.automationDisabled
            ? 'Disabilitata' : automation.executionMode);
        setText('autoOrchestrator', automation.orchestratorState);
        setText('autoAgent', automation.safetyAgentState);
        setText('autoOrchestratorHb', automation.orchestratorLastHeartbeat
            ? new Date(automation.orchestratorLastHeartbeat).toLocaleTimeString('it-IT')
            : 'non disponibile');
        setText('autoAgentHb', automation.safetyAgentLastHeartbeat
            ? new Date(automation.safetyAgentLastHeartbeat).toLocaleTimeString('it-IT')
            : 'non disponibile');
        setText('autoConfig', automation.activeConfigVersion > 0
            ? 'v' + automation.activeConfigVersion : 'nessuna pubblicata');
        setText('autoBlackout', automation.currentBlackoutId ? 'sì' : 'no');
        setText('autoBlackoutDuration', automation.blackoutDurationSeconds !== null
            ? window.UpsCc.formatDuration(automation.blackoutDurationSeconds) : '—');
        setText('autoRuntime', window.UpsCc.formatDuration(automation.remainingRuntimeSeconds));
        setText('autoThreshold', automation.secondsToNextThreshold !== null
            ? window.UpsCc.formatDuration(automation.secondsToNextThreshold) : '—');
    });

    connection.on('InfrastructureStatusChanged', function (infra) {
        setText('infraTotal', infra.totalDevices);
        setText('infraOn', infra.devicesOn);
        setText('infraOff', infra.devicesOff);
        setText('infraShutting', infra.devicesShuttingDown);
        setText('infraStarting', infra.devicesStarting);
        setText('infraUnreachable', infra.devicesUnreachable);
        setText('infraErrors', infra.devicesWithErrors);
        setText('infraUnknown', infra.devicesUnknown);
    });

    connection.on('UpsStatusChanged', function (ups) {
        const card = document.querySelector('[data-ups-id="' + ups.id + '"]');
        if (!card) { return; }

        const badge = card.querySelector('[data-role="status-badge"]');
        if (badge) {
            // Lo stato obsoleto ha la precedenza: mostrare "online" su un dato vecchio
            // sarebbe fuorviante proprio quando conta di più (§5.3).
            if (ups.isStale || !ups.nutConnected) {
                badge.className = 'badge text-bg-secondary';
                badge.textContent = ups.isStale ? 'Dato obsoleto' : 'NUT non connesso';
            } else if (ups.isLowBattery) {
                badge.className = 'badge text-bg-danger';
                badge.textContent = 'Batteria scarica';
            } else if (ups.isOnBattery) {
                badge.className = 'badge text-bg-warning';
                badge.textContent = 'Su batteria';
            } else if (ups.isOnLine) {
                badge.className = 'badge text-bg-success';
                badge.textContent = 'Alimentazione di rete';
            } else {
                badge.className = 'badge text-bg-secondary';
                badge.textContent = 'Stato non determinabile';
            }
        }

        function setRole(role, value) {
            const el = card.querySelector('[data-role="' + role + '"]');
            if (el) { el.textContent = value; }
        }

        setRole('status-text', ups.statusText);
        setRole('charge', window.UpsCc.formatNumber(ups.batteryChargePercent, '%'));
        setRole('runtime', window.UpsCc.formatDuration(ups.batteryRuntimeSeconds));
        setRole('load', window.UpsCc.formatNumber(ups.loadPercent, '%'));
        setRole('vin', window.UpsCc.formatNumber(ups.inputVoltage, 'V'));
        setRole('last-comm', ups.lastCommunicationAt
            ? new Date(ups.lastCommunicationAt).toLocaleString('it-IT') : 'mai');
    });

    connection.onreconnecting(function () {
        if (stateLabel) {
            stateLabel.textContent = 'Connessione in tempo reale: riconnessione in corso…';
        }
    });

    connection.onreconnected(function () {
        if (stateLabel) { stateLabel.textContent = 'Connessione in tempo reale: attiva'; }
    });

    connection.onclose(function () {
        if (stateLabel) {
            stateLabel.textContent = 'Connessione in tempo reale: interrotta (dati non aggiornati)';
        }
    });

    connection.start()
        .then(function () {
            if (stateLabel) { stateLabel.textContent = 'Connessione in tempo reale: attiva'; }
        })
        .catch(function (err) {
            if (stateLabel) {
                stateLabel.textContent = 'Connessione in tempo reale: non disponibile';
            }
            console.error('SignalR non disponibile', err);
        });
})();
