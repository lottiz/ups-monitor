/*
 * Utilità condivise del portale.
 * Le librerie sono servite localmente da wwwroot/lib: la CSP non consente origini esterne e
 * il mini PC può trovarsi in una rete senza accesso a Internet.
 */
(function () {
    'use strict';

    window.UpsCc = window.UpsCc || {};

    const displayTimeZone = document.documentElement.dataset.displayTimeZone || 'Europe/Rome';

    function formatInstant(value, includeDate, includeSeconds) {
        if (!value) { return null; }

        const instant = new Date(value);
        if (Number.isNaN(instant.getTime())) { return null; }

        const options = {
            timeZone: displayTimeZone,
            hour: '2-digit', minute: '2-digit',
            hourCycle: 'h23'
        };
        if (includeSeconds) { options.second = '2-digit'; }
        if (includeDate) {
            options.day = '2-digit';
            options.month = '2-digit';
            options.year = 'numeric';
        }

        return new Intl.DateTimeFormat('it-IT', options).format(instant);
    }

    /** Formatta sempre nel fuso operativo configurato, non in quello casuale del browser. */
    window.UpsCc.formatDateTime = function (value, unavailable) {
        return formatInstant(value, true, true) || (unavailable || '—');
    };

    window.UpsCc.formatTime = function (value, unavailable) {
        return formatInstant(value, false, true) || (unavailable || '—');
    };

    window.UpsCc.formatTimeShort = function (value, unavailable) {
        return formatInstant(value, false, false) || (unavailable || '—');
    };

    /** Mostra una notifica temporanea. Sostituisce alert(), che blocca la pagina. */
    window.UpsCc.toast = function (message, variant) {
        const container = document.getElementById('toastContainer');
        if (!container) { return; }

        const el = document.createElement('div');
        el.className = 'toast align-items-center text-bg-' + (variant || 'secondary');
        el.setAttribute('role', 'alert');
        el.setAttribute('aria-live', 'assertive');
        el.setAttribute('aria-atomic', 'true');
        el.innerHTML =
            '<div class="d-flex">' +
            '<div class="toast-body"></div>' +
            '<button type="button" class="btn-close btn-close-white me-2 m-auto" ' +
            'data-bs-dismiss="toast" aria-label="Chiudi"></button></div>';
        el.querySelector('.toast-body').textContent = message;

        container.appendChild(el);
        const toast = new bootstrap.Toast(el, { delay: 6000 });
        toast.show();
        el.addEventListener('hidden.bs.toast', function () { el.remove(); });
    };

    /** Token antiforgery per le richieste POST (§15). */
    window.UpsCc.antiForgeryToken = function () {
        const input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    };

    window.UpsCc.postJson = async function (url, body) {
        const response = await fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': window.UpsCc.antiForgeryToken()
            },
            body: body === undefined ? null : JSON.stringify(body)
        });

        let payload = null;
        try { payload = await response.json(); } catch (e) { payload = null; }

        return { ok: response.ok, status: response.status, payload: payload };
    };

    /** Formatta i secondi come hh:mm:ss. Un valore assente non diventa "00:00:00". */
    window.UpsCc.formatDuration = function (seconds) {
        if (seconds === null || seconds === undefined) { return 'non disponibile'; }
        const total = Math.max(0, Math.floor(seconds));
        const h = String(Math.floor(total / 3600)).padStart(2, '0');
        const m = String(Math.floor((total % 3600) / 60)).padStart(2, '0');
        const s = String(total % 60).padStart(2, '0');
        return h + ':' + m + ':' + s;
    };

    /** Un valore numerico assente viene reso esplicito, mai come zero (§5.1). */
    window.UpsCc.formatNumber = function (value, unit, decimals) {
        if (value === null || value === undefined) { return 'non disponibile'; }
        return Number(value).toFixed(decimals === undefined ? 1 : decimals) + ' ' + (unit || '');
    };

    /** Aggiorna il badge della modalità nella barra di navigazione. */
    window.UpsCc.updateModeBadge = function (automation) {
        const badge = document.getElementById('modeBadge');
        if (!badge || !automation) { return; }

        if (automation.automationDisabled) {
            badge.className = 'badge mode-pill text-bg-danger';
            badge.textContent = 'Automazioni disabilitate';
        } else if (automation.executionMode === 'DryRun' ||
                   automation.executionMode === 'DRY_RUN' ||
                   automation.executionMode === 0) {
            badge.className = 'badge mode-pill text-bg-info';
            badge.textContent = 'Modalità: DRY_RUN';
        } else if (automation.executionMode === 'Live' ||
                   automation.executionMode === 'LIVE' ||
                   automation.executionMode === 1) {
            badge.className = 'badge mode-pill text-bg-warning';
            badge.textContent = 'Modalità: LIVE';
        } else {
            badge.className = 'badge mode-pill text-bg-secondary';
            badge.textContent = 'Modalità: non determinata';
        }
    };

    // Il badge si popola a ogni pagina: la modalità deve essere sempre visibile.
    document.addEventListener('DOMContentLoaded', function () {
        fetch('/api/dashboard/automation')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { if (data) { window.UpsCc.updateModeBadge(data); } })
            .catch(function () { /* badge non aggiornato: non è un errore bloccante */ });
    });
})();
