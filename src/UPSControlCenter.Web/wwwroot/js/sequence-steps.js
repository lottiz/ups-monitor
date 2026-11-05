/* Editor degli step di una sequenza: elenco, modale di modifica, verifica dipendenze (§10). */
(function () {
    'use strict';

    const kind = window.__sequenceKind;
    const sequenceId = window.__sequenceId;
    const modalEl = document.getElementById('stepModal');
    const modal = new bootstrap.Modal(modalEl);

    const table = new Tabulator('#stepsTable', {
        ajaxURL: '/Sequences/Steps?kind=' + kind + '&sequenceId=' + sequenceId,
        layout: 'fitColumns',
        initialSort: [{ column: 'orderIndex', dir: 'asc' }],
        placeholder: 'Nessuno step configurato.',
        columns: [
            { title: 'Ordine', field: 'orderIndex', width: 90, hozAlign: 'right' },
            { title: 'Nome', field: 'name', minWidth: 160 },
            { title: 'Tipo', field: 'stepType', width: 150 },
            {
                title: 'Bersaglio', field: 'targetDeviceName', minWidth: 160,
                formatter: cell => {
                    const row = cell.getRow().getData();
                    return row.targetDeviceName || row.targetGroupName ||
                        '<span class="text-body-secondary">nessuno</span>';
                }
            },
            { title: 'Gruppo parallelo', field: 'parallelGroup', width: 130,
                formatter: cell => cell.getValue() === null ? '—' : cell.getValue() },
            { title: 'Timeout (s)', field: 'timeoutSeconds', width: 110, hozAlign: 'right' },
            { title: 'Retry', field: 'maxRetries', width: 90, hozAlign: 'right' },
            {
                title: 'Abilitato', field: 'isEnabled', width: 100, hozAlign: 'center',
                formatter: cell => cell.getValue() ? 'sì' : 'no'
            },
            {
                title: 'Azioni', field: 'id', width: 170, headerSort: false,
                formatter: () => '<div class="btn-group btn-group-sm">' +
                    '<button class="btn btn-outline-secondary" data-action="edit">Modifica</button>' +
                    '<button class="btn btn-outline-danger" data-action="delete">Elimina</button></div>',
                cellClick: function (e, cell) {
                    const action = e.target.getAttribute('data-action');
                    const row = cell.getRow().getData();
                    if (action === 'edit') { openModal(row); }
                    if (action === 'delete') { deleteStep(row, cell); }
                }
            }
        ]
    });

    function openModal(row) {
        document.getElementById('stepId').value = row ? row.id : '';
        document.getElementById('stepName').value = row ? row.name : '';
        document.getElementById('stepType').value = row ? row.stepType : 0;
        document.getElementById('stepOrder').value = row ? row.orderIndex : (nextOrderIndex());
        document.getElementById('stepParallelGroup').value = row && row.parallelGroup !== null ? row.parallelGroup : '';
        document.getElementById('stepTargetDevice').value = row && row.targetDeviceId ? row.targetDeviceId : '';
        document.getElementById('stepTargetGroup').value = row && row.targetGroupId ? row.targetGroupId : '';
        document.getElementById('stepDelay').value = row ? row.delayBeforeSeconds : 0;
        document.getElementById('stepTimeout').value = row ? row.timeoutSeconds : 300;
        document.getElementById('stepMaxRetries').value = row ? row.maxRetries : 0;
        document.getElementById('stepContinueOnError').checked = row ? row.continueOnError : false;
        document.getElementById('stepVerifyCompletion').checked = row ? row.verifyCompletion : true;
        document.getElementById('stepEnabled').checked = row ? row.isEnabled : true;
        modal.show();
    }

    function nextOrderIndex() {
        const data = table.getData();
        return data.length ? Math.max(...data.map(d => d.orderIndex)) + 10 : 10;
    }

    document.getElementById('addStep').addEventListener('click', function () { openModal(null); });

    document.getElementById('saveStepBtn').addEventListener('click', async function () {
        const payload = {
            id: document.getElementById('stepId').value || null,
            orderIndex: Number(document.getElementById('stepOrder').value),
            name: document.getElementById('stepName').value,
            stepType: Number(document.getElementById('stepType').value),
            targetDeviceId: document.getElementById('stepTargetDevice').value || null,
            targetGroupId: document.getElementById('stepTargetGroup').value || null,
            parallelGroup: document.getElementById('stepParallelGroup').value !== ''
                ? Number(document.getElementById('stepParallelGroup').value) : null,
            delayBeforeSeconds: Number(document.getElementById('stepDelay').value),
            timeoutSeconds: Number(document.getElementById('stepTimeout').value),
            maxRetries: Number(document.getElementById('stepMaxRetries').value),
            continueOnError: document.getElementById('stepContinueOnError').checked,
            verifyCompletion: document.getElementById('stepVerifyCompletion').checked,
            excludedDeviceIds: [],
            isEnabled: document.getElementById('stepEnabled').checked
        };

        if (!payload.name) { window.UpsCc.toast('Il nome dello step è obbligatorio.', 'warning'); return; }

        const result = await window.UpsCc.postJson(
            '/Sequences/SaveStep?kind=' + kind + '&sequenceId=' + sequenceId, payload);

        if (result.ok) {
            modal.hide();
            table.replaceData();
            window.UpsCc.toast('Step salvato.', 'success');
        } else {
            window.UpsCc.toast(
                result.payload && result.payload.message ? result.payload.message : 'Salvataggio non riuscito.',
                'danger');
        }
    });

    async function deleteStep(row, cell) {
        if (!window.confirm('Eliminare lo step «' + row.name + '»?')) { return; }

        const result = await window.UpsCc.postJson(
            '/Sequences/DeleteStep?kind=' + kind + '&stepId=' + row.id);

        if (result.ok) {
            cell.getRow().delete();
            window.UpsCc.toast('Step eliminato.', 'success');
        } else {
            window.UpsCc.toast('Eliminazione non riuscita.', 'danger');
        }
    }

    document.getElementById('validateDeps').addEventListener('click', async function () {
        const result = await window.UpsCc.postJson(
            '/Sequences/ValidateDependencies?kind=' + kind + '&sequenceId=' + sequenceId);

        const box = document.getElementById('validationResult');
        box.classList.remove('d-none');

        if (!result.ok || !result.payload) {
            box.className = 'alert alert-danger';
            box.textContent = 'Verifica non riuscita.';
            return;
        }

        const payload = result.payload;
        if (payload.isValid) {
            box.className = 'alert alert-success';
            box.innerHTML = '<strong>Nessuna dipendenza circolare.</strong> Ordine risultante: ' +
                payload.shutdownOrderDeviceNames.join(' → ');
        } else {
            box.className = 'alert alert-danger';
            box.innerHTML = '<strong>Dipendenze non valide:</strong><ul class="mb-0">' +
                payload.errors.map(e => '<li>' + e + '</li>').join('') + '</ul>';
        }
    });
})();
