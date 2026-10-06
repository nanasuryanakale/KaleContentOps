/**
 * Phase 2B - Master PIC lifecycle management screen.
 * Server-rendered forms handle Create/Edit (so validation feedback comes from
 * the controller); this file only owns modal open/close and the lifecycle
 * actions (Nonaktifkan / Aktifkan) which talk to the existing JSON endpoints.
 *
 * Aktifkan posts to /MasterPic/Toggle, which blocks a closed employment period
 * (ResignDate set) with a clear business error - the message is surfaced here
 * instead of silently reopening the period.
 */
(function () {
    'use strict';

    var page = document.querySelector('.master-pic-page');
    if (!page) return;

    var table = document.getElementById('mpTable');
    var createModal = document.getElementById('mpCreateModal');
    var editModal = document.getElementById('mpEditModal');

    function closeModals() {
        if (createModal) createModal.hidden = true;
        if (editModal) editModal.hidden = true;
    }

    function openModal(modal) {
        if (!modal) return;
        closeModals();
        modal.hidden = false;
    }

    // Page-level banner for client-side action errors (mirrors .mp-banner styles).
    function showBanner(message) {
        var existing = document.getElementById('mpClientBanner');
        if (existing) existing.parentNode.removeChild(existing);

        var banner = document.createElement('div');
        banner.className = 'mp-banner mp-banner-error';
        banner.id = 'mpClientBanner';
        banner.textContent = message;
        page.insertBefore(banner, page.firstChild);
        banner.scrollIntoView({ block: 'nearest' });
    }

    var btnNew = document.getElementById('mpBtnNew');
    if (btnNew) {
        btnNew.addEventListener('click', function () {
            openModal(createModal);
        });
    }

    page.addEventListener('click', function (e) {
        var target = e.target;
        if (target && target.closest && target.closest('[data-mp-close]')) {
            closeModals();
        }
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') closeModals();
    });

    // ---------------- Edit ----------------
    Array.prototype.forEach.call(page.querySelectorAll('.js-mp-edit'), function (btn) {
        btn.addEventListener('click', function () {
            var id = btn.getAttribute('data-id');
            var row = table ? table.querySelector('tr[data-pic="' + id + '"]') : null;
            if (!row) return;

            var name = row.getAttribute('data-name') || '';
            document.getElementById('mpEditId').value = id;
            document.getElementById('mpEditName').value = name;
            document.getElementById('mpEditJoin').value = row.getAttribute('data-join') || '';
            document.getElementById('mpEditResign').value = row.getAttribute('data-resign') || '';
            document.getElementById('mpEditTitle').textContent = 'Edit PIC \u2014 ' + name;
            openModal(editModal);
        });
    });

    // ---------------- Deactivate / resign ----------------
    Array.prototype.forEach.call(page.querySelectorAll('.js-mp-deactivate'), function (btn) {
        btn.addEventListener('click', function () {
            var id = btn.getAttribute('data-id');
            if (!id) return;

            var confirmed = window.confirm(
                'Nonaktifkan PIC ini? Masa kerja akan ditutup dengan tanggal bisnis hari ini (Asia/Jakarta).'
            );
            if (!confirmed) return;

            fetch('/MasterPic/Deactivate?id=' + encodeURIComponent(id), { method: 'POST' })
                .then(function (res) {
                    if (!res.ok) {
                        return res.text().then(function (text) { throw new Error(text); });
                    }
                    window.location.reload();
                })
                .catch(function (err) {
                    showBanner('Gagal menonaktifkan PIC: ' + err.message);
                });
        });
    });

    // ---------------- Reactivate (safe) ----------------
    Array.prototype.forEach.call(page.querySelectorAll('.js-mp-reactivate'), function (btn) {
        btn.addEventListener('click', function () {
            var id = btn.getAttribute('data-id');
            if (!id) return;

            fetch('/MasterPic/Toggle?id=' + encodeURIComponent(id), { method: 'POST' })
                .then(function (res) {
                    if (res.ok) {
                        window.location.reload();
                        return null;
                    }
                    return res.json().then(function (json) {
                        showBanner(json.error || 'PIC tidak dapat diaktifkan kembali.');
                    });
                })
                .catch(function (err) {
                    showBanner('Gagal mengaktifkan PIC: ' + err.message);
                });
        });
    });
})();
