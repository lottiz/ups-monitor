/* Registro di audit: elenco filtrabile, in sola lettura (§4.4, §18). */
(function () {
    'use strict';

    new Tabulator('#auditTable', {
        ajaxURL: '/Audit/List?limit=500',
        layout: 'fitColumns',
        pagination: true,
        paginationSize: 25,
        placeholder: 'Nessuna voce di audit.',
        initialSort: [{ column: 'occurredAt', dir: 'desc' }],
        columns: [
            {
                title: 'Data e ora', field: 'occurredAt', width: 180,
                formatter: cell => new Date(cell.getValue()).toLocaleString('it-IT')
            },
            { title: 'Utente', field: 'userName', width: 160, headerFilter: 'input' },
            { title: 'Azione', field: 'action', minWidth: 180, headerFilter: 'input' },
            { title: 'Entità', field: 'entityType', width: 160, headerFilter: 'input' },
            { title: 'Id', field: 'entityId', width: 140 },
            {
                title: 'Esito', field: 'outcome', width: 110,
                formatter: cell => {
                    const v = cell.getValue();
                    const cls = v === 'Success' ? 'success' : (v === 'Refused' || v === 'Rejected' ? 'danger' : 'secondary');
                    return '<span class="badge text-bg-' + cls + '">' + v + '</span>';
                }
            },
            { title: 'Motivazione', field: 'reason', minWidth: 220 },
            { title: 'IP', field: 'ipAddress', width: 130 }
        ]
    });
})();
