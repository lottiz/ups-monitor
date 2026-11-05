/* Builder visuale delle condizioni (§9.2). */
(function () {
    'use strict';

    // Enum ConditionKind lato client, allineato a UPSControlCenter.Core.Rules.ConditionKind.
    const CONDITION_KINDS = [
        { value: 0, label: 'Durata blackout (secondi)', numeric: true },
        { value: 1, label: 'Flag stato UPS (es. OB, LB)', numeric: false, text: true, textPlaceholder: 'OB' },
        { value: 2, label: 'Autonomia residua (secondi)', numeric: true },
        { value: 3, label: 'Carica batteria (%)', numeric: true },
        { value: 4, label: 'Carico UPS (%)', numeric: true },
        { value: 5, label: 'Fascia oraria', numeric: false, text: true, textPlaceholder: '22:00-06:00' },
        { value: 7, label: 'Comunicazione NUT persa', boolean: true },
        { value: 8, label: 'Dato obsoleto', boolean: true },
        { value: 9, label: 'Durata blackout incerta', boolean: true }
    ];

    const OPERATORS = [
        { value: 0, label: '=' }, { value: 1, label: '≠' }, { value: 2, label: '>' },
        { value: 3, label: '≥' }, { value: 4, label: '<' }, { value: 5, label: '≤' }
    ];

    let rowIndex = 0;
    const container = document.getElementById('conditionRows');

    function addRow(initial) {
        const idx = rowIndex++;
        const row = document.createElement('div');
        row.className = 'row g-2 align-items-end mb-2 condition-row';
        row.dataset.index = String(idx);

        const kindOptions = CONDITION_KINDS.map(function (k) {
            const sel = initial && initial.condition === k.value ? ' selected' : '';
            return '<option value="' + k.value + '"' + sel + '>' + k.label + '</option>';
        }).join('');

        const opOptions = OPERATORS.map(function (o) {
            const sel = initial && initial.operator === o.value ? ' selected' : '';
            return '<option value="' + o.value + '"' + sel + '>' + o.label + '</option>';
        }).join('');

        row.innerHTML =
            '<div class="col-12 col-md-4">' +
            '<select class="form-select condition-kind" name="SimpleConditions[' + idx + '].Condition">' +
            kindOptions + '</select></div>' +
            '<div class="col-6 col-md-2 op-cell"><select class="form-select condition-op" ' +
            'name="SimpleConditions[' + idx + '].Operator">' + opOptions + '</select></div>' +
            '<div class="col-6 col-md-4 value-cell"><input class="form-control condition-value" /></div>' +
            '<div class="col-12 col-md-2">' +
            '<button type="button" class="btn btn-outline-danger w-100 remove-row">Rimuovi</button></div>';

        container.appendChild(row);

        const kindSelect = row.querySelector('.condition-kind');
        kindSelect.addEventListener('change', function () { updateRowFields(row); });
        row.querySelector('.remove-row').addEventListener('click', function () { row.remove(); });

        updateRowFields(row, initial);
    }

    function updateRowFields(row, initial) {
        const idx = row.dataset.index;
        const kindValue = Number(row.querySelector('.condition-kind').value);
        const kind = CONDITION_KINDS.find(function (k) { return k.value === kindValue; });

        const opCell = row.querySelector('.op-cell');
        const valueCell = row.querySelector('.value-cell');

        if (kind.boolean) {
            opCell.innerHTML = '';
            valueCell.innerHTML = '<div class="form-control-plaintext small text-body-secondary">' +
                'vero/falso, nessun valore necessario' +
                '<input type="hidden" name="SimpleConditions[' + idx + '].NumericValue" value="" /></div>';
            return;
        }

        if (kind.text) {
            opCell.innerHTML = '<input type="hidden" name="SimpleConditions[' + idx + '].Operator" value="0" />';
            const value = initial && initial.textValue ? initial.textValue : '';
            valueCell.innerHTML = '<input class="form-control" name="SimpleConditions[' + idx + '].TextValue" ' +
                'value="' + value + '" placeholder="' + kind.textPlaceholder + '" />';
            return;
        }

        // Numerico: operatore + valore.
        if (!opCell.querySelector('.condition-op')) {
            const opOptions = OPERATORS.map(function (o) { return '<option value="' + o.value + '">' + o.label + '</option>'; }).join('');
            opCell.innerHTML = '<select class="form-select condition-op" name="SimpleConditions[' + idx + '].Operator">' + opOptions + '</select>';
        }
        opCell.querySelector('select').name = 'SimpleConditions[' + idx + '].Operator';
        if (initial && initial.operator !== undefined) { opCell.querySelector('select').value = String(initial.operator); }

        const numValue = initial && initial.numericValue !== undefined && initial.numericValue !== null
            ? initial.numericValue : '';
        valueCell.innerHTML = '<input type="number" step="any" class="form-control" ' +
            'name="SimpleConditions[' + idx + '].NumericValue" value="' + numValue + '" />';
    }

    document.getElementById('addCondition').addEventListener('click', function () { addRow(); });

    // Righe esistenti, se si sta modificando una regola già salvata.
    const existingEl = document.getElementById('existingSimpleConditions');
    if (existingEl) {
        try {
            const existing = JSON.parse(existingEl.textContent) || [];
            existing.forEach(function (row) {
                addRow({
                    condition: row.condition, operator: row.operator,
                    numericValue: row.numericValue, textValue: row.textValue
                });
            });
        } catch (e) { /* nessuna riga preesistente valida */ }
    }

    if (rowIndex === 0) { addRow(); }

    // Toggle modalità avanzata.
    const advancedToggle = document.getElementById('advancedToggle');
    const simpleBuilder = document.getElementById('simpleBuilder');
    const advancedBuilder = document.getElementById('advancedBuilder');

    advancedToggle.addEventListener('change', function () {
        simpleBuilder.classList.toggle('d-none', advancedToggle.checked);
        advancedBuilder.classList.toggle('d-none', !advancedToggle.checked);
    });

    function collectFormState() {
        const rows = [...container.querySelectorAll('.condition-row')].map(function (row) {
            const kindValue = Number(row.querySelector('.condition-kind').value);
            const opEl = row.querySelector('select[name$=".Operator"]') || row.querySelector('input[name$=".Operator"]');
            const numEl = row.querySelector('input[name$=".NumericValue"]');
            const textEl = row.querySelector('input[name$=".TextValue"]');
            return {
                condition: kindValue,
                operator: opEl ? Number(opEl.value) : 0,
                numericValue: numEl && numEl.value !== '' ? Number(numEl.value) : null,
                textValue: textEl ? textEl.value : null
            };
        });

        return {
            useAdvancedMode: advancedToggle.checked,
            topOperator: Number(document.querySelector('[name="TopOperator"]').value),
            simpleConditions: rows,
            rawConditionJson: document.querySelector('[name="RawConditionJson"]').value,
            name: document.querySelector('[name="Name"]').value || 'anteprima'
        };
    }

    document.getElementById('previewBtn').addEventListener('click', async function () {
        const output = document.getElementById('previewOutput');
        output.classList.remove('d-none');

        const response = await fetch('/Rules/PreviewCondition', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': window.UpsCc.antiForgeryToken()
            },
            body: JSON.stringify(collectFormState())
        });

        const payload = await response.json().catch(function () { return null; });

        output.textContent = response.ok
            ? JSON.stringify(JSON.parse(payload.json), null, 2)
            : (payload && payload.message ? payload.message : 'Condizione non valida.');
    });
})();
