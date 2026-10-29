/* Tabella dispositivi: ricerca, filtri, ordinamento, paginazione, esportazione, azioni (§18). */
(function () {
    'use strict';

    const table = new Tabulator('#devicesTable', {
        ajaxURL: '/Devices/List',
        layout: 'fitColumns',
        pagination: true,
        paginationSize: 25,
        paginationSizeSelector: [10, 25, 50, 100],
        placeholder: 'Nessun dispositivo configurato.',
        initialSort: [{ column: 'priority', dir: 'asc' }],
        columns: [
            { title: 'Nome', field: 'name', minWidth: 160, headerFilter: 'input' },
            { title: 'Hostname', field: 'hostname', minWidth: 160 },
            { title: 'Indirizzo IP', field: 'ipAddress', width: 140 },
            { title: 'Tipologia', field: 'deviceType', width: 150 },
            { title: 'Gruppo', field: 'deviceGroupName', width: 140 },
            { title: 'Priorità', field: 'priority', width: 100, hozAlign: 'right' },
            {
                title: 'Alimentazione', field: 'powerSources', minWidth: 160, headerSort: false,
                formatter: function (cell) {
                    const sources = cell.getValue() || [];
                    if (sources.length === 0) {
                        // Un dispositivo senza sorgente non è protetto: va evidenziato.
                        return '<span class="text-danger">nessun UPS associato</span>';
                    }
                    return sources.map(function (s) {
                        return s.isPrimary ? '<strong>' + s.upsName + '</strong>' : s.upsName;
                    }).join(', ');
                }
            },
            { title: 'Spegnimento', field: 'shutdownProtocol', width: 130 },
            { title: 'Accensione', field: 'startupProtocol', width: 130 },
            {
                title: 'Stato', field: 'lastKnownState', width: 140,
                formatter: function (cell) {
                    const v = cell.getValue();
                    const map = {
                        On: 'success', Off: 'dark', ShuttingDown: 'warning',
                        Starting: 'info', Unreachable: 'warning', Error: 'danger',
                        Unknown: 'secondary'
                    };
                    return '<span class="badge text-bg-' + (map[v] || 'secondary') + '">' + v + '</span>';
                }
            },
            {
                title: 'Abilitato', field: 'isEnabled', width: 110, hozAlign: 'center',
                formatter: function (cell) {
                    return cell.getValue()
                        ? '<span class="text-success" aria-label="Abilitato">sì</span>'
                        : '<span class="text-body-secondary" aria-label="Disabilitato">no</span>';
                }
            },
            {
                title: 'Azioni', field: 'id', width: 200, headerSort: false,
                formatter: function () {
                    return '<div class="btn-group btn-group-sm">' +
                        '<button class="btn btn-outline-secondary" data-action="edit">Modifica</button>' +
                        '<button class="btn btn-outline-secondary" data-action="test">Test</button>' +
                        '<button class="btn btn-outline-danger" data-action="delete">Elimina</button>' +
                        '</div>';
                },
                cellClick: function (e, cell) {
                    const action = e.target.getAttribute('data-action');
                    if (!action) { return; }

                    const row = cell.getRow().getData();

                    if (action === 'edit') {
                        window.location.href = '/Devices/Edit/' + row.id;
                        return;
                    }

                    if (action === 'test') { testConnection(row); return; }
                    if (action === 'delete') { deleteDevice(row, cell); }
                }
            }
        ]
    });

    async function testConnection(device) {
        const result = await window.UpsCc.postJson('/Devices/TestConnection?id=' + device.id);

        if (!result.ok) {
            window.UpsCc.toast('Test non eseguito: ' + result.status, 'danger');
            return;
        }

        const p = result.payload;
        // Il test in DRY_RUN non apre connessioni: lo si dichiara invece di far credere
        // che la raggiungibilità sia stata verificata.
        const prefix = p.wasDryRun ? '[DRY_RUN] ' : '';
        window.UpsCc.toast(prefix + p.message, p.success ? 'success' : 'warning');
    }

    async function deleteDevice(device, cell) {
        if (!window.confirm('Eliminare il dispositivo «' + device.name + '»?')) { return; }

        const result = await window.UpsCc.postJson('/Devices/Delete?id=' + device.id);

        if (result.ok) {
            cell.getRow().delete();
            window.UpsCc.toast('Dispositivo eliminato.', 'success');
            return;
        }

        const message = result.payload && result.payload.message
            ? result.payload.message
            : 'Eliminazione non riuscita.';
        window.UpsCc.toast(message, 'danger');
    }

    function applyFilters() {
        const text = document.getElementById('filterText').value.trim();
        const type = document.getElementById('filterType').value;
        const state = document.getElementById('filterState').value;

        table.clearFilter();

        if (text) {
            table.addFilter(function (data) {
                const needle = text.toLowerCase();
                return (data.name || '').toLowerCase().includes(needle)
                    || (data.hostname || '').toLowerCase().includes(needle)
                    || (data.ipAddress || '').toLowerCase().includes(needle);
            });
        }

        if (type) { table.addFilter('deviceType', '=', type); }
        if (state) { table.addFilter('lastKnownState', '=', state); }
    }

    ['filterText', 'filterType', 'filterState'].forEach(function (id) {
        const el = document.getElementById(id);
        if (el) { el.addEventListener('input', applyFilters); }
    });

    const exportBtn = document.getElementById('exportCsv');
    if (exportBtn) {
        exportBtn.addEventListener('click', function () {
            table.download('csv', 'dispositivi.csv', { delimiter: ';' });
        });
    }
})();
