// Admin TikTok - persistent batch video sync (date range).
// Starts a background job (POST /Admin/TikTok/StartBatchSync), polls its status
// (GET /Admin/TikTok/BatchSyncStatus/{id}) every 2s while active, stops polling when the
// job reaches a terminal state, and renders the job history table.
(function () {
    'use strict';

    var form = document.getElementById('batchSyncForm');
    if (!form) return;

    var errorBox = document.getElementById('batchSyncError');
    var statusBox = document.getElementById('batchSyncStatus');
    var startButton = document.getElementById('batchSyncButton');
    var historyBody = document.querySelector('#batchHistoryTable tbody');
    var pollTimer = null;
    var currentJobId = null;

    function el(id) { return document.getElementById(id); }

    function showError(message) {
        errorBox.textContent = message || '';
        errorBox.style.display = message ? 'block' : 'none';
    }

    function antiForgeryToken() {
        var input = form.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function formatDate(value) {
        if (!value) return '';
        return String(value).substring(0, 10);
    }

    function isoDate(date) {
        if (!(date instanceof Date)) date = new Date(date);
        var year = date.getFullYear();
        var month = String(date.getMonth() + 1).padStart(2, '0');
        var day = String(date.getDate()).padStart(2, '0');
        return year + '-' + month + '-' + day;
    }

    function parseIso(value) {
        return new Date(value + 'T00:00:00');
    }

    // Same display format as the shared Daily Summary date picker ("14 Okt 2026").
    function formatDisplayDate(value) {
        if (!value) return '';
        return parseIso(value).toLocaleDateString('id-ID', { day: '2-digit', month: 'short', year: 'numeric' });
    }

    // ---- date range picker initialization -------------------------------------------------------
    var datePickerState = { start: null, end: null };
    var datePickerButton = el('batchDateRangeButton');
    var datePickerPanel = el('batchDatePickerPanel');
    var datePickerSelected = el('batchDatePickerSelected');

    function updateDateRangeDisplay() {
        if (datePickerState.start && datePickerState.end) {
            el('batchDateRangeLabel').textContent = formatDisplayDate(datePickerState.start) + ' - ' + formatDisplayDate(datePickerState.end);
            datePickerSelected.textContent = formatDisplayDate(datePickerState.start) + ' s/d ' + formatDisplayDate(datePickerState.end);
        } else {
            el('batchDateRangeLabel').textContent = 'Pilih rentang';
            datePickerSelected.textContent = '';
        }
    }

    function openDatePicker() {
        datePickerPanel.hidden = false;
        datePickerButton.setAttribute('aria-expanded', 'true');
        if (!rangeCalendar) {
            initializeCalendar();
        } else {
            renderPendingLabel();
            rangeCalendar.render();
        }
    }

    function closeDatePicker() {
        datePickerPanel.hidden = true;
        datePickerButton.setAttribute('aria-expanded', 'false');
    }

    // Keeps the popup inside the viewport whatever the trigger's position: it is normally
    // anchored at the trigger's left edge (left: 0), but is right-aligned to the trigger when a
    // left-aligned panel would run past the right edge, and pulled back in if it would run past
    // the left edge. Without this the 680px panel overflows a narrow window and makes the whole
    // page scroll horizontally.
    function positionDatePicker() {
        if (datePickerPanel.hidden) return;
        datePickerPanel.style.left = '';
        var anchor = datePickerButton.getBoundingClientRect();
        var panelWidth = datePickerPanel.offsetWidth || 0;
        if (!panelWidth || !anchor.width) return;

        var margin = 12;
        var offset = 0;
        if (anchor.left + panelWidth > window.innerWidth - margin) {
            // Not enough room to the right of the trigger: align the panel's right edge to it.
            offset = anchor.right - panelWidth - anchor.left;
        }
        var minOffset = margin - anchor.left;
        if (offset < minOffset) offset = minOffset;

        datePickerPanel.style.left = Math.round(offset) + 'px';
    }

    datePickerButton.addEventListener('click', function () {
        openDatePicker();
        positionDatePicker();
    });
    el('batchCloseDatePicker').addEventListener('click', closeDatePicker);
    el('batchCancelDatePicker').addEventListener('click', closeDatePicker);
    window.addEventListener('resize', positionDatePicker);

    // ---- calendar initialization using shared CalendarPopup component -------------------------------------------------------
    // Integrates with calendar-popup.js to use the shared date range picker pattern
    // matching DailySummary and other reporting pages.
    var rangeCalendar;

    function renderPendingLabel() {
        var label = el('batchPendingRangeLabel');
        if (datePickerState.start && datePickerState.end) {
            label.textContent = formatDisplayDate(datePickerState.start) + ' - ' + formatDisplayDate(datePickerState.end);
        } else if (datePickerState.start && !datePickerState.end) {
            label.textContent = formatDisplayDate(datePickerState.start) + ' - ...';
        } else {
            label.textContent = 'Pilih rentang tanggal';
        }
        // Also update the footer short label
        var footer = el('batchDatePickerSelected');
        if (footer) {
            if (datePickerState.start && datePickerState.end) {
                footer.textContent = formatDisplayDate(datePickerState.start) + ' s/d ' + formatDisplayDate(datePickerState.end);
            } else if (datePickerState.start) {
                footer.textContent = formatDisplayDate(datePickerState.start) + ' - ...';
            } else {
                footer.textContent = '';
            }
        }
    }

    function initializeCalendar() {
        rangeCalendar = CalendarPopup.createRangeCalendar({
            grid: el('batchCalendarGrid'),
            titleEl: el('batchCalendarTitle'),
            prevButton: el('batchPreviousMonth'),
            nextButton: el('batchNextMonth'),
            months: 2,
            initialMonth: datePickerState.start ? parseIso(datePickerState.start) : new Date(),
            getSelection: function () {
                return { start: datePickerState.start, end: datePickerState.end };
            },
            onSelectionChange: function (start, end) {
                datePickerState.start = start;
                datePickerState.end = end;
                renderPendingLabel();
            }
        });
        rangeCalendar.render();
    }

    // ---- preset buttons -------------------------------------------------------
    var presetButtons = document.querySelectorAll('#batchPresetList button[data-preset]');
    presetButtons.forEach(function (btn) {
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            var preset = this.getAttribute('data-preset');
            var endDate = new Date();
            endDate.setHours(0, 0, 0, 0);
            var startDate = new Date(endDate);

            if (preset === 'today') {
                // today only
            } else if (preset === 'yesterday') {
                startDate.setDate(startDate.getDate() - 1);
                endDate.setDate(endDate.getDate() - 1);
            } else {
                var days = parseInt(preset, 10);
                startDate.setDate(startDate.getDate() - days + 1);
            }

            datePickerState.start = isoDate(startDate);
            datePickerState.end = isoDate(endDate);
            renderPendingLabel();
            if (rangeCalendar) {
                rangeCalendar.render();
            }
            updateDateRangeDisplay();
        });
    });

    // ---- apply date picker -------------------------------------------------------
    el('batchApplyDatePicker').addEventListener('click', function (e) {
        e.preventDefault();
        if (!datePickerState.start || !datePickerState.end) {
            alert('Pilih rentang tanggal sebelum menerapkan.');
            return;
        }
        el('batchStartDate').value = datePickerState.start;
        el('batchEndDate').value = datePickerState.end;
        // Keep the trigger label in sync with the applied range (Daily Summary parity).
        updateDateRangeDisplay();
        closeDatePicker();
    });

    // Click-outside + Escape close, matching the shared date-picker behaviour. Calendar day
    // clicks already stop propagation inside the shared component, so this cannot close the
    // panel while the user is selecting a range.
    document.addEventListener('click', function (event) {
        if (datePickerPanel.hidden) return;
        var target = event.target instanceof Element ? event.target : null;
        if (!target) return;
        if (!datePickerPanel.contains(target) && !datePickerButton.contains(target)) closeDatePicker();
    });
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') closeDatePicker();
    });

    // ---- start job -------------------------------------------------------
    form.addEventListener('submit', function (event) {
        event.preventDefault();
        showError('');

        var shopId = el('batchShopId').value;
        var startDate = el('batchStartDate').value;
        var endDate = el('batchEndDate').value;

        if (!shopId) { showError('Pilih authorized shop terlebih dahulu.'); return; }
        if (!startDate || !endDate) { showError('Pilih Start Date dan End Date terlebih dahulu.'); return; }
        if (endDate < startDate) { showError('End Date harus pada atau setelah Start Date.'); return; }

        startButton.disabled = true;

        var token = antiForgeryToken();
        var body = new URLSearchParams();
        body.append('shopId', shopId);
        body.append('startDate', startDate);
        body.append('endDate', endDate);
        body.append('__RequestVerificationToken', token);

        fetch(form.action, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded',
                'RequestVerificationToken': token
            },
            body: body.toString()
        })
            .then(function (response) {
                return response.json()
                    .catch(function () { return null; })
                    .then(function (data) {
                        if (!response.ok) {
                            throw new Error((data && data.error) || ('Gagal memulai job (HTTP ' + response.status + ').'));
                        }
                        return data;
                    });
            })
            .then(function (data) {
                currentJobId = data.jobId;
                statusBox.style.display = 'block';
                startPolling();
                loadHistory();
            })
            .catch(function (error) { showError(error.message); })
            .finally(function () { startButton.disabled = false; });
    });

    // ---- status polling ---------------------------------------------------
    function startPolling() {
        stopPolling();
        pollOnce();
        pollTimer = setInterval(pollOnce, 2000);
    }

    function stopPolling() {
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
    }

    function pollOnce() {
        if (!currentJobId) { stopPolling(); return; }

        fetch('/Admin/TikTok/BatchSyncStatus/' + currentJobId, { headers: { 'Accept': 'application/json' } })
            .then(function (response) {
                if (!response.ok) throw new Error('Status tidak tersedia (HTTP ' + response.status + ').');
                return response.json();
            })
            .then(function (status) {
                renderStatus(status);
                // Keep the history table in sync with the live job so the detail panel and the
                // history row can never show a different phase/status for the same job.
                loadHistory();
                if (!status.isActive) { stopPolling(); }
            })
            .catch(function () { /* transient network error: keep polling */ });
    }

    function renderStatus(status) {
        el('bsJobId').textContent = status.jobId;
        el('bsPhase').textContent = status.phase;
        el('bsShop').textContent = status.shopName || ('#' + status.shopId);
        el('bsPeriod').textContent = formatDate(status.startDate) + ' s/d ' + formatDate(status.endDate) + ' (inklusif)';
        el('bsPages').textContent = status.totalPagesCollected;
        el('bsTotal').textContent = status.totalVideoIdsFound;
        el('bsPending').textContent = status.pending;
        el('bsProcessing').textContent = status.processing;
        el('bsSucceeded').textContent = status.succeeded;
        el('bsFailed').textContent = status.failed;
        // Items the job never attempted (rate-limit budget exhausted / job abandoned): a distinct
        // state from Failed, so the UI never presents an untried video as a real failure.
        el('bsNotAttempted').textContent = status.notAttempted;
        el('bsUnclassified').textContent = status.unclassifiedVideos;

        var done = status.succeeded + status.failed;
        var percent = status.totalVideoIdsFound > 0
            ? Math.round((done / status.totalVideoIdsFound) * 100)
            : (status.collectionCompleted && !status.isActive ? 100 : 0);
        var bar = el('bsProgressBar');
        bar.style.width = percent + '%';
        bar.textContent = percent + '%';

        if (!status.collectionCompleted && status.isActive) {
            bar.classList.add('progress-bar-striped', 'progress-bar-animated');
        } else {
            bar.classList.remove('progress-bar-animated');
        }
        bar.classList.toggle('bg-danger', status.failed > 0);
        bar.classList.toggle('bg-success', status.failed === 0 && status.isActive === false);

        el('bsLastError').textContent = status.lastError ? ('Error: ' + status.lastError) : '';

        // NextAttemptAt is rendered server-side in the shop reporting timezone with an explicit
        // label (e.g. "14 Okt 2026 15:30 GMT+7"), so no manual UTC/offset math lives in the UI.
        var nextAttemptText = '';
        if (status.nextAttemptAtDisplay) {
            nextAttemptText = 'Percobaan berikutnya: ' + status.nextAttemptAtDisplay + ' (attempt ' + status.attemptCount + ')';
        }
        el('bsNextAttempt').textContent = nextAttemptText;

        var failedHtml = '';
        if (status.failedItems && status.failedItems.length) {
            failedHtml = '<strong>Item gagal (sampel 20 terakhir):</strong><ul style="margin: 2px 0 0 16px; padding: 0;">';
            for (var i = 0; i < status.failedItems.length; i++) {
                var item = status.failedItems[i];
                failedHtml += '<li>' + item.videoId + ' — ' + (item.lastError || 'unknown error') + '</li>';
            }
            failedHtml += '</ul>';
        }
        el('bsFailedItems').innerHTML = failedHtml;
    }

    // ---- history -----------------------------------------------------------
    function loadHistory() {
        fetch('/Admin/TikTok/BatchSyncHistory?take=10', { headers: { 'Accept': 'application/json' } })
            .then(function (response) { return response.ok ? response.json() : []; })
            .then(function (jobs) {
                if (!historyBody) return;
                historyBody.innerHTML = '';
                for (var i = 0; i < jobs.length; i++) {
                    var job = jobs[i];
                    var row = document.createElement('tr');
                    row.innerHTML =
                        '<td>#' + job.jobId + '</td>' +
                        '<td>' + (job.shopName || ('#' + job.shopId)) + '</td>' +
                        '<td>' + formatDate(job.startDate) + ' → ' + formatDate(job.endDate) + '</td>' +
                        '<td>' + (job.phase || job.status) + '</td>' +
                        '<td>' + job.totalVideoIdsFound + '</td>' +
                        '<td>' + job.pending + '</td>' +
                        '<td>' + job.processing + '</td>' +
                        '<td>' + job.succeeded + '</td>' +
                        '<td>' + job.failed + '</td>' +
                        '<td>' + job.notAttempted + '</td>' +
                        '<td>' + (job.createdAtDisplay || formatDate(job.createdAt) || '') + '</td>';
                    historyBody.appendChild(row);
                }
            })
            .catch(function () { /* history is non-critical */ });
    }

    // Initial history load (before any job is started).
    loadHistory();
})();
