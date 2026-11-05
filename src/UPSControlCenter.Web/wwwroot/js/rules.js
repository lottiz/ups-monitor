/* Elenco regole di spegnimento (§9.2, §18). */
(function () {
    'use strict';

    const table = new Tabulator('#rulesTable', {
        ajaxURL: '/Rules/List',
        layout: 'fitColumns',
        placeholder: 'Nessuna regola configurata: si applicano solo le soglie di base della policy.',
        initialSort: [{ column: 'priority', dir: 'asc' }],
        columns: [
            { title: 'Priorità', field: 'priority', width: 100, hozAlign: 'right' },
            { title: 'Nome', field: 'name', minWidth: 180, headerFilter: 'input' },
            { title: 'Sequenza', field: 'triggerSequenceName', minWidth: 160,
                formatter: cell => cell.getValue() || '<span class="text-body-secondary">predefinita</span>' },
            {
                title: 'Fascia oraria', field: 'hasTimeWindows', width: 120, hozAlign: 'center',
                formatter: cell => cell.getValue() ? 'sì' : '—'
            },
            {
                title: 'Giorni', field: 'hasDayRestriction', width: 100, hozAlign: 'center',
                formatter: cell => cell.getValue() ? 'limitati' : 'tutti'
            },
            {
                title: 'Abilitata', field: 'isEnabled', width: 110, hozAlign: 'center',
                formatter: cell => cell.getValue()
                    ? '<span class="text-success">sì</span>'
                    : '<span class="text-body-secondary">no</span>'
            },
            {
                title: 'Azioni', field: 'id', width: 180, headerSort: false,
                formatter: () => '<div class="btn-group btn-group-sm">' +
                    '<button class="btn btn-outline-secondary" data-action="edit">Modifica</button>' +
                    '<button class="btn btn-outline-danger" data-action="delete">Elimina</button></div>',
                cellClick: function (e, cell) {
                    const action = e.target.getAttribute('data-action');
                    const row = cell.getRow().getData();
                    if (action === 'edit') { window.location.href = '/Rules/Edit/' + row.id; }
                    if (action === 'delete') { deleteRule(row, cell); }
                }
            }
        ]
    });

    async function deleteRule(row, cell) {
        if (!window.confirm('Eliminare la regola «' + row.name + '»?')) { return; }

        const result = await window.UpsCc.postJson('/Rules/Delete?id=' + row.id);
        if (result.ok) {
            cell.getRow().delete();
            window.UpsCc.toast('Regola eliminata.', 'success');
        } else {
            window.UpsCc.toast('Eliminazione non riuscita.', 'danger');
        }
    }
})();
