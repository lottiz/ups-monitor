/* Elenco sequenze (spegnimento o riaccensione, §10, §12). */
(function () {
    'use strict';

    const kind = window.__sequenceKind;

    const table = new Tabulator('#sequencesTable', {
        ajaxURL: '/Sequences/List?kind=' + kind,
        layout: 'fitColumns',
        placeholder: 'Nessuna sequenza configurata.',
        columns: [
            { title: 'Nome', field: 'name', minWidth: 200, headerFilter: 'input' },
            { title: 'Descrizione', field: 'description', minWidth: 220 },
            { title: 'Step', field: 'stepCount', width: 100, hozAlign: 'right' },
            {
                title: 'Abilitata', field: 'isEnabled', width: 110, hozAlign: 'center',
                formatter: cell => cell.getValue()
                    ? '<span class="text-success">sì</span>'
                    : '<span class="text-body-secondary">no</span>'
            },
            {
                title: 'Azioni', field: 'id', width: 220, headerSort: false,
                formatter: () => '<div class="btn-group btn-group-sm">' +
                    '<button class="btn btn-outline-secondary" data-action="steps">Step</button>' +
                    '<button class="btn btn-outline-secondary" data-action="edit">Modifica</button>' +
                    '<button class="btn btn-outline-danger" data-action="delete">Elimina</button></div>',
                cellClick: function (e, cell) {
                    const action = e.target.getAttribute('data-action');
                    const row = cell.getRow().getData();
                    if (action === 'steps') {
                        window.location.href = '/Sequences/EditSteps?kind=' + kind + '&id=' + row.id;
                    } else if (action === 'edit') {
                        window.location.href = '/Sequences/Edit?kind=' + kind + '&id=' + row.id;
                    } else if (action === 'delete') {
                        deleteSequence(row, cell);
                    }
                }
            }
        ]
    });

    async function deleteSequence(row, cell) {
        if (!window.confirm('Eliminare la sequenza «' + row.name + '»? Tutti gli step verranno rimossi.')) {
            return;
        }

        const result = await window.UpsCc.postJson(
            '/Sequences/DeleteSequence?kind=' + kind + '&id=' + row.id);

        if (result.ok) {
            cell.getRow().delete();
            window.UpsCc.toast('Sequenza eliminata.', 'success');
        } else {
            window.UpsCc.toast('Eliminazione non riuscita: verificare che non sia in uso.', 'danger');
        }
    }
})();
