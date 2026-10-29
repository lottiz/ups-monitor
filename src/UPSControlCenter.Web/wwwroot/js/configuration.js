/* Versioni di configurazione: anteprima, pubblicazione, rollback (§3.3). */
(function () {
    'use strict';

    const stateLabels = {
        0: 'Bozza', 1: 'Validata', 2: 'Pubblicata',
        3: 'Superata', 4: 'Ripristinata', 5: 'Rifiutata'
    };

    let versions = [];
    const dataEl = document.getElementById('versionsData');
    if (dataEl) {
        try { versions = JSON.parse(dataEl.textContent) || []; }
        catch (e) { versions = []; }
    }

    new Tabulator('#versionsTable', {
        data: versions,
        layout: 'fitColumns',
        pagination: true,
        paginationSize: 15,
        placeholder: 'Nessuna versione di configurazione.',
        initialSort: [{ column: 'version', dir: 'desc' }],
        columns: [
            { title: 'Versione', field: 'version', width: 110, hozAlign: 'right' },
            {
                title: 'Stato', field: 'state', width: 140,
                formatter: function (cell) {
                    const v = cell.getValue();
                    const label = stateLabels[v] || v;
                    const cls = (v === 2 || v === 'Published') ? 'success'
                        : ((v === 5 || v === 'Rejected') ? 'danger' : 'secondary');
                    return '<span class="badge text-bg-' + cls + '">' + label + '</span>';
                }
            },
            { title: 'Creata da', field: 'createdBy', width: 180 },
            {
                title: 'Creata il', field: 'createdAt', width: 170,
                formatter: function (cell) {
                    return cell.getValue() ? new Date(cell.getValue()).toLocaleString('it-IT') : '—';
                }
            },
            {
                title: 'Confermata', field: 'acknowledgedAt', width: 170,
                formatter: function (cell) {
                    return cell.getValue()
                        ? new Date(cell.getValue()).toLocaleString('it-IT')
                        : 'in attesa';
                }
            },
            {
                title: 'Attivata', field: 'activatedAt', width: 170,
                formatter: function (cell) {
                    return cell.getValue()
                        ? new Date(cell.getValue()).toLocaleString('it-IT')
                        : 'in attesa';
                }
            },
            { title: 'Attivata da', field: 'activatedByAgent', width: 180 },
            {
                title: 'Checksum', field: 'checksumSha256', width: 140,
                formatter: function (cell) {
                    const v = cell.getValue() || '';
                    return v ? '<code>' + v.substring(0, 12) + '…</code>' : '—';
                }
            },
            {
                title: 'Azioni', field: 'version', width: 130, headerSort: false,
                formatter: function () {
                    return '<button class="btn btn-sm btn-outline-secondary" ' +
                        'data-action="rollback">Ripristina</button>';
                },
                cellClick: function (e, cell) {
                    if (e.target.getAttribute('data-action') !== 'rollback') { return; }
                    rollback(cell.getRow().getData().version);
                }
            }
        ]
    });

    async function rollback(version) {
        const reason = window.prompt(
            'Motivazione del ripristino alla versione ' + version + ' (obbligatoria):');
        if (!reason) { return; }

        const params = new URLSearchParams({ version: version, reason: reason });
        const response = await fetch('/Configuration/Rollback?' + params.toString(), {
            method: 'POST',
            headers: { 'RequestVerificationToken': window.UpsCc.antiForgeryToken() }
        });

        const payload = await response.json().catch(function () { return null; });

        if (response.ok) {
            window.UpsCc.toast(payload.message, 'success');
            setTimeout(function () { window.location.reload(); }, 1500);
            return;
        }

        window.UpsCc.toast(
            payload && payload.errors ? payload.errors.join(' ') : 'Ripristino non riuscito.',
            'danger');
    }

    document.getElementById('previewBtn').addEventListener('click', async function () {
        const response = await fetch('/Configuration/Preview');
        const payload = await response.json().catch(function () { return null; });

        const output = document.getElementById('previewOutput');
        output.classList.remove('d-none');

        if (!payload) {
            output.textContent = 'Anteprima non disponibile.';
            return;
        }

        const lines = [];
        lines.push('Pubblicabile: ' + (payload.canPublish ? 'sì' : 'no'));

        if (payload.message) { lines.push('Nota: ' + payload.message); }
        if (payload.version) { lines.push('Versione proposta: ' + payload.version); }
        if (payload.executionMode !== undefined) {
            lines.push('Modalità: ' + payload.executionMode);
        }
        if (payload.deviceCount !== undefined) {
            lines.push('Dispositivi: ' + payload.deviceCount + ' · Step: ' + payload.stepCount);
        }
        if (payload.errors && payload.errors.length) {
            lines.push('', 'ERRORI:');
            payload.errors.forEach(function (e) { lines.push(' - ' + e); });
        }
        if (payload.warnings && payload.warnings.length) {
            lines.push('', 'AVVISI:');
            payload.warnings.forEach(function (w) { lines.push(' - ' + w); });
        }
        if (payload.payload) {
            lines.push('', 'PAYLOAD CANONICO:', payload.payload);
        }

        output.textContent = lines.join('\n');
    });

    document.getElementById('publishBtn').addEventListener('click', async function () {
        const reason = document.getElementById('publishReason').value.trim();

        if (!window.confirm(
            'Pubblicare la configurazione? Il Safety Agent la applicherà alla prossima ' +
            'sincronizzazione.')) {
            return;
        }

        const params = new URLSearchParams({ reason: reason });
        const response = await fetch('/Configuration/Publish?' + params.toString(), {
            method: 'POST',
            headers: { 'RequestVerificationToken': window.UpsCc.antiForgeryToken() }
        });

        const payload = await response.json().catch(function () { return null; });

        if (response.ok) {
            window.UpsCc.toast(payload.message, 'success');
            if (payload.warnings) {
                payload.warnings.forEach(function (w) { window.UpsCc.toast(w, 'warning'); });
            }
            setTimeout(function () { window.location.reload(); }, 2000);
            return;
        }

        if (payload && payload.errors) {
            payload.errors.forEach(function (e) { window.UpsCc.toast(e, 'danger'); });
        } else {
            window.UpsCc.toast('Pubblicazione non riuscita.', 'danger');
        }
    });
})();
