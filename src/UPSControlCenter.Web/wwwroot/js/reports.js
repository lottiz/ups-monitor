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
            { title: 'Stato', field: 'statusRaw', minWidth: 120 },
            {
                title: 'Obsoleto', field: 'isStale', width: 100,
                formatter: cell => cell.getValue() ? 'sì' : 'no'
            }
        ]
    });

    function buildQuery() {
        const params = new URLSearchParams({
            upsId: upsSelect.value,
            from: fromInput.value,
            to: toInput.value,
            sampleMinutes: intervalInput.value
        });
        return '/Reports/List?' + params.toString();
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
            await table.setData(buildQuery());
        } catch (e) {
            window.UpsCc.toast('Impossibile caricare il report.', 'danger');
        }
    });

    document.getElementById('reportDownload').addEventListener('click', function () {
        table.download('csv', 'report-ups.csv');
    });
})();
