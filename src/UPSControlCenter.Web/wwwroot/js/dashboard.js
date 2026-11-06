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
                    return window.UpsCc.formatDateTime(cell.getValue());
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
    const stateDot = document.getElementById('connectionDot');

    const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/dashboard')
        .withAutomaticReconnect()
        .build();

    function setText(id, text) {
        const el = document.getElementById(id);
        if (el) { el.textContent = text; }
    }

    function setConnectionState(text, state) {
        if (stateLabel) { stateLabel.textContent = text; }
        if (stateDot) { stateDot.className = 'connection-dot connection-' + state; }
    }

    function executionModeLabel(mode) {
        if (mode === 0 || mode === 'DryRun' || mode === 'DRY_RUN') { return 'DRY_RUN'; }
        if (mode === 1 || mode === 'Live' || mode === 'LIVE') { return 'LIVE'; }
        return 'non determinata';
    }

    connection.on('AutomationStatusChanged', function (automation) {
        window.UpsCc.updateModeBadge(automation);

        setText('autoMode', automation.automationDisabled
            ? 'Disabilitata' : executionModeLabel(automation.executionMode));
        setText('autoOrchestrator', automation.orchestratorState);
        setText('autoAgent', automation.safetyAgentState);
        setText('autoOrchestratorHb', automation.orchestratorLastHeartbeat
            ? window.UpsCc.formatTime(automation.orchestratorLastHeartbeat)
            : 'non disponibile');
        setText('autoAgentHb', automation.safetyAgentLastHeartbeat
            ? window.UpsCc.formatTime(automation.safetyAgentLastHeartbeat)
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
                badge.className = 'badge status-pill text-bg-secondary';
                badge.textContent = ups.isStale ? 'Dato obsoleto' : 'NUT non connesso';
            } else if (ups.isLowBattery) {
                badge.className = 'badge status-pill text-bg-danger';
                badge.textContent = 'Batteria scarica';
            } else if (ups.isOnBattery) {
                badge.className = 'badge status-pill text-bg-warning';
                badge.textContent = 'Su batteria';
            } else if (ups.isOnLine) {
                badge.className = 'badge status-pill text-bg-success';
                badge.textContent = 'Alimentazione di rete';
            } else {
                badge.className = 'badge status-pill text-bg-secondary';
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
            ? window.UpsCc.formatDateTime(ups.lastCommunicationAt) : 'mai');
    });

    connection.onreconnecting(function () {
        setConnectionState('Connessione in tempo reale: riconnessione in corso…', 'wait');
    });

    connection.onreconnected(function () {
        setConnectionState('Connessione in tempo reale: attiva', 'on');
    });

    connection.onclose(function () {
        setConnectionState('Connessione in tempo reale: interrotta (dati non aggiornati)', 'off');
    });

    connection.start()
        .then(function () {
            setConnectionState('Connessione in tempo reale: attiva', 'on');
        })
        .catch(function (err) {
            setConnectionState('Connessione in tempo reale: non disponibile', 'off');
            console.error('SignalR non disponibile', err);
        });

    // Riaccensioni in attesa di approvazione manuale (§12).
    function loadPendingStartups() {
        fetch('/api/admin/startup/pending')
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (items) {
                const card = document.getElementById('pendingStartupsCard');
                const list = document.getElementById('pendingStartupsList');
                if (!card || !list) { return; }

                if (!items.length) {
                    card.classList.add('d-none');
                    return;
                }

                card.classList.remove('d-none');
                list.innerHTML = '';

                items.forEach(function (item) {
                    const row = document.createElement('div');
                    row.className = 'd-flex justify-content-between align-items-center border rounded p-2 mb-2';

                    const info = document.createElement('div');
                    info.innerHTML = '<div class="fw-semibold">Blackout ' +
                        (item.blackoutId || '—') + '</div>' +
                        '<div class="small text-body-secondary">' + item.triggerReason + '</div>';

                    const button = document.createElement('button');
                    button.className = 'btn btn-sm btn-primary';
                    button.textContent = item.approvedAt ? 'Approvata, in attesa dell\'orchestrator' : 'Approva';
                    button.disabled = !!item.approvedAt;
                    button.addEventListener('click', function () {
                        approveStartup(item.id, button);
                    });

                    row.appendChild(info);
                    row.appendChild(button);
                    list.appendChild(row);
                });
            })
            .catch(function () { /* non bloccante: la dashboard resta utilizzabile */ });
    }

    async function approveStartup(runId, button) {
        button.disabled = true;
        const result = await window.UpsCc.postJson('/api/admin/startup/' + runId + '/approve');

        if (result.ok) {
            window.UpsCc.toast(result.payload.message, 'success');
            loadPendingStartups();
            return;
        }

        button.disabled = false;
        window.UpsCc.toast(
            result.payload && result.payload.message ? result.payload.message : 'Approvazione non riuscita.',
            'danger');
    }

    loadPendingStartups();
    setInterval(loadPendingStartups, 15000);
})();
