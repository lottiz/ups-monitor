/* Grafico storico e variabili NUT grezze. */
(function () {
    'use strict';

    const canvas = document.getElementById('historyChart');
    if (canvas) {
        const upsId = canvas.getAttribute('data-ups-id');

        fetch('/api/dashboard/measurements/' + upsId + '?hours=24')
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (data) {
                const labels = data.map(function (d) {
                    return window.UpsCc.formatTimeShort(d.recordedAt);
                });

                new Chart(canvas, {
                    type: 'line',
                    data: {
                        labels: labels,
                        datasets: [
                            {
                                label: 'Carica batteria (%)',
                                // I null restano null: Chart.js interrompe la linea, mostrando
                                // onestamente i buchi nei dati invece di interpolarli.
                                data: data.map(function (d) { return d.batteryChargePercent; }),
                                borderColor: '#0d6efd',
                                spanGaps: false,
                                tension: 0.2,
                                yAxisID: 'y'
                            },
                            {
                                label: 'Carico UPS (%)',
                                data: data.map(function (d) { return d.loadPercent; }),
                                borderColor: '#fd7e14',
                                spanGaps: false,
                                tension: 0.2,
                                yAxisID: 'y'
                            },
                            {
                                label: 'Autonomia residua (minuti)',
                                data: data.map(function (d) {
                                    return d.batteryRuntimeSeconds === null
                                        ? null
                                        : Math.round(d.batteryRuntimeSeconds / 60);
                                }),
                                borderColor: '#198754',
                                spanGaps: false,
                                tension: 0.2,
                                yAxisID: 'y1'
                            }
                        ]
                    },
                    options: {
                        responsive: true,
                        interaction: { mode: 'index', intersect: false },
                        scales: {
                            y: {
                                type: 'linear', position: 'left', min: 0, max: 100,
                                title: { display: true, text: 'Percentuale' }
                            },
                            y1: {
                                type: 'linear', position: 'right', min: 0,
                                grid: { drawOnChartArea: false },
                                title: { display: true, text: 'Minuti' }
                            }
                        }
                    }
                });
            })
            .catch(function () {
                window.UpsCc.toast('Storico non disponibile.', 'warning');
            });
    }

    const rawBtn = document.getElementById('loadRaw');
    if (rawBtn) {
        rawBtn.addEventListener('click', async function () {
            const upsId = rawBtn.getAttribute('data-ups-id');
            const response = await fetch('/api/dashboard/ups/' + upsId + '/raw');
            const output = document.getElementById('rawOutput');

            if (!response.ok) {
                output.textContent = 'Nessuna misura disponibile per questo UPS.';
                return;
            }

            const payload = await response.json();
            output.textContent = JSON.stringify(payload, null, 2);
        });
    }
})();
