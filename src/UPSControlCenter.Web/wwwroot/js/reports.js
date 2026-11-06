/* Report storico: carico, autonomia residua e altre metriche, periodo e intervallo liberi (§18). */
(function () {
    'use strict';

    function toDateInputValue(date) {
        return date.toISOString().slice(0, 10);
    }

    const form = document.getElementById('reportFilters');
    const fromInput = document.getElementById('reportFrom');
    const toInput = document.getElementById('reportTo');
    const intervalInput = document.getElementById('reportInterval');
    const upsSelect = document.getElementById('reportUps');

    // Periodo predefinito: ultimi 7 giorni, così il report mostra subito qualcosa.
    const today = new Date();
    const weekAgo = new Date(today.getTime() - 7 * 24 * 60 * 60 * 1000);
    toInput.value = toDateInputValue(today);
    fromInput.value = toDateInputValue(weekAgo);

    const table = new Tabulator('#reportTable', {
        layout: 'fitColumns',
        pagination: true,
        paginationSize: 50,
        placeholder: 'Imposta UPS, periodo e intervallo, poi premi Genera.',
        initialSort: [{ column: 'recordedAt', dir: 'desc' }],
        columns: [
            {
                title: 'Data e ora', field: 'recordedAt', width: 180,
                formatter: cell => window.UpsCc.formatDateTime(cell.getValue())
            },
            {
                title: 'Carico', field: 'loadPercent', width: 110,
                formatter: cell => window.UpsCc.formatNumber(cell.getValue(), '%')
            },
            {
                title: 'Autonomia residua', field: 'batteryRuntimeSeconds', width: 150,
                formatter: cell => window.UpsCc.formatDuration(cell.getValue())
            },
            {
                title: 'Carica batteria', field: 'batteryChargePercent', width: 130,
                formatter: cell => window.UpsCc.formatNumber(cell.getValue(), '%')
            },
            {
                title: 'Tensione ingresso', field: 'inputVoltage', width: 140,
                formatter: cell => window.UpsCc.formatNumber(cell.getValue(), 'V')
            },
            {
                title: 'Tensione uscita', field: 'outputVoltage', width: 140,
                formatter: cell => window.UpsCc.formatNumber(cell.getValue(), 'V')
            },
            {
                title: 'Corrente', field: 'outputCurrentAmps', width: 110,
                formatter: cell => window.UpsCc.formatNumber(cell.getValue(), 'A')
            },
            { title: 'Stato', field: 'statusRaw', minWidth: 120 },
            {
                title: 'Obsoleto', field: 'isStale', width: 100,
                formatter: cell => cell.getValue() ? 'sì' : 'no'
            }
        ]
    });

    const summaryBox = document.getElementById('reportSummary');
    const avgLoadEl = document.getElementById('reportAvgLoad');
    const avgRuntimeEl = document.getElementById('reportAvgRuntime');
    const avgCurrentEl = document.getElementById('reportAvgCurrent');
    const sampleCountEl = document.getElementById('reportSampleCount');
    const chartCard = document.getElementById('reportChartCard');
    let reportChart = null;

    function periodParams() {
        return { upsId: upsSelect.value, from: fromInput.value, to: toInput.value };
    }

    async function loadRows() {
        const params = new URLSearchParams(Object.assign(periodParams(), {
            sampleMinutes: intervalInput.value
        }));
        const response = await fetch('/Reports/List?' + params.toString());
        if (!response.ok) { throw new Error('HTTP ' + response.status); }
        return response.json();
    }

    async function loadSummary() {
        const params = new URLSearchParams(periodParams());
        const response = await fetch('/Reports/Summary?' + params.toString());
        if (!response.ok) { throw new Error('HTTP ' + response.status); }
        const summary = await response.json();

        avgLoadEl.textContent = window.UpsCc.formatNumber(summary.avgLoadPercent, '%');
        avgRuntimeEl.textContent = window.UpsCc.formatDuration(
            summary.avgBatteryRuntimeSeconds === null ? null : Math.round(summary.avgBatteryRuntimeSeconds));
        avgCurrentEl.textContent = window.UpsCc.formatNumber(summary.avgOutputCurrentAmps, 'A');
        sampleCountEl.textContent = summary.sampleCount;
        summaryBox.classList.remove('d-none');
    }

    function renderChart(rows) {
        if (typeof Chart === 'undefined') { return; }

        if (reportChart) { reportChart.destroy(); }

        if (rows.length === 0) {
            chartCard.classList.add('d-none');
            return;
        }
        chartCard.classList.remove('d-none');

        const labels = rows.map(r => window.UpsCc.formatDateTime(r.recordedAt));

        reportChart = new Chart(document.getElementById('reportChart'), {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    {
                        label: 'Corrente (A)',
                        data: rows.map(r => r.outputCurrentAmps),
                        borderColor: '#0b91ae',
                        backgroundColor: 'rgba(24, 184, 217, 0.12)',
                        borderWidth: 2, pointRadius: 0, fill: true, tension: 0.25,
                        yAxisID: 'y'
                    },
                    {
                        label: 'Carico (%)',
                        data: rows.map(r => r.loadPercent),
                        borderColor: '#f08a24',
                        backgroundColor: 'rgba(240, 138, 36, 0.08)',
                        borderWidth: 2, pointRadius: 0, fill: true, tension: 0.25,
                        yAxisID: 'y1'
                    }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                scales: {
                    x: { ticks: { maxTicksLimit: 10, maxRotation: 0 } },
                    y: { position: 'left', beginAtZero: true, title: { display: true, text: 'A' } },
                    y1: { position: 'right', beginAtZero: true, max: 100, grid: { display: false }, title: { display: true, text: '%' } }
                }
            }
        });
    }

    form.addEventListener('submit', async function (event) {
        event.preventDefault();

        if (!upsSelect.value) {
            window.UpsCc.toast('Seleziona un UPS.', 'warning');
            return;
        }
        if (toInput.value < fromInput.value) {
            window.UpsCc.toast('Il periodo non è valido: "A" precede "Da".', 'warning');
            return;
        }

        try {
            const rows = await loadRows();
            table.setData(rows);
            renderChart(rows);
            await loadSummary();
        } catch (e) {
            window.UpsCc.toast('Impossibile caricare il report.', 'danger');
        }
    });

    document.getElementById('reportDownload').addEventListener('click', function () {
        table.download('csv', 'report-ups.csv');
    });
})();
