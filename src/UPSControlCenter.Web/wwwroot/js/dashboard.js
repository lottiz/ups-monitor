/* Dashboard: aggiornamenti in tempo reale via SignalR e tabella eventi. */
(function () {
    'use strict';

    const eventsTable = new Tabulator('#eventsTable', {
        ajaxURL: '/api/dashboard/events?limit=200',
        layout: 'fitColumns',
        responsiveLayout: 'collapse',
        pagination: true,
        paginationSize: 15,
        placeholder: 'Nessun evento registrato.',
        columns: [
            {
                title: 'Data e ora', field: 'occurredAt', width: 180, responsive: 0,
                formatter: function (cell) {
                    return window.UpsCc.formatDateTime(cell.getValue());
                }
            },
            { title: 'Tipo', field: 'eventType', headerFilter: 'input', responsive: 0 },
            {
                title: 'Gravità', field: 'severity', width: 130, headerFilter: 'list', responsive: 1,
                headerFilterParams: { values: ['Information', 'Warning', 'Critical'] },
                formatter: function (cell) {
                    const v = cell.getValue();
                    const cls = v === 'Critical' ? 'danger' : (v === 'Warning' ? 'warning' : 'secondary');
                    return '<span class="badge text-bg-' + cls + '">' + v + '</span>';
                }
            },
            { title: 'Stato UPS', field: 'newStatus', width: 140, responsive: 2 },
            { title: 'Blackout', field: 'blackoutId', headerFilter: 'input', responsive: 4 },
            { title: 'Rilevato da', field: 'detectedBy', width: 140, responsive: 3 }
        ]
    });

    // Ricarica periodica: copre anche il caso in cui SignalR non sia disponibile.
    setInterval(function () { eventsTable.replaceData(); }, 30000);

    const stateLabel = document.getElementById('connectionState');
    const stateDot = document.getElementById('connectionDot');
    const trendUps = document.getElementById('trendUps');
    const trendLoading = document.getElementById('trendLoading');
    const trendCharts = document.getElementById('trendCharts');
    const trendEmpty = document.getElementById('trendEmpty');
    let runtimeChart = null;
    let capacityChart = null;
    let voltageChart = null;
    let infrastructureChart = null;

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

    function destroyChart(chart) {
        if (chart) { chart.destroy(); }
    }

    function lineOptions(yTitle, suggestedMin, suggestedMax) {
        return {
            responsive: true,
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            animation: { duration: 350 },
            plugins: {
                legend: { labels: { usePointStyle: true, boxWidth: 8 } },
                tooltip: { displayColors: true }
            },
            scales: {
                x: {
                    grid: { display: false },
                    ticks: { maxTicksLimit: 8, color: '#758398', maxRotation: 0 }
                },
                y: {
                    suggestedMin: suggestedMin,
                    suggestedMax: suggestedMax,
                    beginAtZero: suggestedMin === 0,
                    grid: { color: 'rgba(133, 151, 175, 0.16)' },
                    ticks: { color: '#758398' },
                    title: { display: true, text: yTitle, color: '#66758a' }
                }
            }
        };
    }

    function lineDataset(label, data, color, background, yAxisId) {
        return {
            label: label,
            data: data,
            borderColor: color,
            backgroundColor: background,
            borderWidth: 2.5,
            pointRadius: 0,
            pointHoverRadius: 4,
            fill: true,
            tension: 0.28,
            spanGaps: false,
            yAxisID: yAxisId || 'y'
        };
    }

    /* Due assi indipendenti: le serie non condividono unità (minuti vs. percentuale) e
       forzarle sulla stessa scala appiattirebbe una delle due. */
    function dualAxisOptions(leftTitle, rightTitle) {
        const options = lineOptions(leftTitle, 0);
        options.scales.y.position = 'left';
        options.scales.y1 = {
            position: 'right',
            suggestedMin: 0,
            suggestedMax: 100,
            beginAtZero: true,
            grid: { display: false },
            ticks: { color: '#758398' },
            title: { display: true, text: rightTitle, color: '#66758a' }
        };
        return options;
    }

    function renderTrendCharts(data) {
        const labels = data.map(function (point) {
            return window.UpsCc.formatTimeShort(point.recordedAt);
        });

        destroyChart(runtimeChart);
        destroyChart(capacityChart);
        destroyChart(voltageChart);

        runtimeChart = new Chart(document.getElementById('runtimeTrendChart'), {
            type: 'line',
            data: {
                labels: labels,
                datasets: [lineDataset(
                    'Autonomia (min)',
                    data.map(function (point) {
                        return point.batteryRuntimeSeconds === null
                            ? null
                            : Math.round(point.batteryRuntimeSeconds / 60);
                    }),
                    '#0b91ae',
                    'rgba(24, 184, 217, 0.15)')]
            },
            options: lineOptions('minuti', 0)
        });

        capacityChart = new Chart(document.getElementById('capacityTrendChart'), {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    lineDataset('Autonomia (min)', data.map(function (point) {
                        return point.batteryRuntimeSeconds === null
                            ? null
                            : Math.round(point.batteryRuntimeSeconds / 60);
                    }), '#0b91ae', 'rgba(24, 184, 217, 0.12)', 'y'),
                    lineDataset('Carico (%)', data.map(function (point) {
                        return point.loadPercent;
                    }), '#f08a24', 'rgba(240, 138, 36, 0.08)', 'y1')
                ]
            },
            options: dualAxisOptions('minuti', '%')
        });

        voltageChart = new Chart(document.getElementById('voltageTrendChart'), {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    lineDataset('Ingresso', data.map(function (point) {
                        return point.inputVoltage;
                    }), '#6f55d9', 'rgba(111, 85, 217, 0.08)'),
                    lineDataset('Uscita', data.map(function (point) {
                        return point.outputVoltage;
                    }), '#dd4c7c', 'rgba(221, 76, 124, 0.06)')
                ]
            },
            options: lineOptions('volt')
        });
    }

    function loadTrendCharts() {
        if (!trendUps || typeof Chart === 'undefined') { return; }

        if (trendLoading) { trendLoading.classList.remove('d-none'); }
        if (trendCharts) { trendCharts.classList.add('trend-is-loading'); }
        if (trendEmpty) { trendEmpty.classList.add('d-none'); }

        fetch('/api/dashboard/measurements/' + encodeURIComponent(trendUps.value) +
              '?hours=24&sampleMinutes=20')
            .then(function (response) {
                if (!response.ok) { throw new Error('HTTP ' + response.status); }
                return response.json();
            })
            .then(function (data) {
                if (!data.length) {
                    destroyChart(runtimeChart);
                    destroyChart(capacityChart);
                    destroyChart(voltageChart);
                    runtimeChart = capacityChart = voltageChart = null;
                    if (trendCharts) { trendCharts.classList.add('d-none'); }
                    if (trendEmpty) { trendEmpty.classList.remove('d-none'); }
                    return;
                }
                if (trendCharts) { trendCharts.classList.remove('d-none'); }
                renderTrendCharts(data);
            })
            .catch(function () {
                if (trendCharts) { trendCharts.classList.add('d-none'); }
                if (trendEmpty) {
                    trendEmpty.textContent = 'Grafici temporaneamente non disponibili.';
                    trendEmpty.classList.remove('d-none');
                }
            })
            .finally(function () {
                if (trendLoading) { trendLoading.classList.add('d-none'); }
                if (trendCharts) { trendCharts.classList.remove('trend-is-loading'); }
            });
    }

    function infrastructureValues() {
        function value(id) {
            const element = document.getElementById(id);
            return element ? Number(element.textContent) || 0 : 0;
        }
        return [
            value('infraOn'), value('infraOff'), value('infraShutting'),
            value('infraStarting'), value('infraUnreachable'),
            value('infraErrors'), value('infraUnknown')
        ];
    }

    function updateInfrastructureChart() {
        const canvas = document.getElementById('infrastructureChart');
        if (!canvas || typeof Chart === 'undefined') { return; }

        const values = infrastructureValues();
        if (!infrastructureChart) {
            infrastructureChart = new Chart(canvas, {
                type: 'doughnut',
                data: {
                    labels: ['Accesi', 'Spenti', 'In arresto', 'In avvio',
                        'Non raggiungibili', 'Con errori', 'Ignoto'],
                    datasets: [{
                        data: values,
                        backgroundColor: ['#20b77a', '#26364b', '#f0ad35', '#18b8d9',
                            '#e98324', '#dc4862', '#8b98a8'],
                        borderColor: '#ffffff',
                        borderWidth: 3,
                        hoverOffset: 5
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    cutout: '64%',
                    plugins: {
                        legend: {
                            position: 'bottom',
                            labels: { usePointStyle: true, boxWidth: 8, padding: 12 }
                        }
                    }
                }
            });
            return;
        }

        infrastructureChart.data.datasets[0].data = values;
        infrastructureChart.update();
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
        updateInfrastructureChart();
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
        setRole('current', window.UpsCc.formatNumber(ups.outputCurrentAmps, 'A'));
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

    if (trendUps) {
        trendUps.addEventListener('change', loadTrendCharts);
        loadTrendCharts();
        // Il campione visivo ha cadenza venti minuti; le card sopra restano live via SignalR.
        setInterval(loadTrendCharts, 20 * 60 * 1000);
    }
    updateInfrastructureChart();

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
