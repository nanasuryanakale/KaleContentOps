/**
 * Menu Targets UI — Auto-save functionality
 * Scope: Targets page only (event handlers scoped to .targets-page)
 * 
 * Features:
 * - Load current targets + actual data on page load
 * - Auto-save on blur and Enter key
 * - Client-side calculation of Selisih and Status
 * - Toast notification on success
 * - Error handling with user feedback
 * - Antiforgery token validation
 */

(function() {
    'use strict';

    // ========== Constants ==========
    const API_CURRENT = '/targets/current';
    const API_SAVE = '/targets/save';
    const DEBOUNCE_MS = 300;
    const TOAST_DURATION_MS = 2500;

    // Status labels and colors (per business rule)
    const STATUS_LABELS = {
        above: 'Di atas target',
        met: 'Mencapai target',
        below: 'Di bawah target'
    };

    const STATUS_CLASSES = {
        above: 'above-target',
        met: 'target-met',
        below: 'below-target'
    };

    // ========== State ==========
    const state = {
        currentTargets: {},  // { [contentTypeId]: { ...target } }
        actuals: {},         // { [contentTypeId]: { ...actual } }
        pendingSave: {},     // { [contentTypeId]: { type: 'upload|views', value, ... } }
        lastSavedValues: {}  // Track what was last saved to prevent duplicate requests
    };

    // ========== Initialization ==========
    document.addEventListener('DOMContentLoaded', async function() {
        console.log('Targets page loaded');

        // Extract antiforgery token
        const tokenElement = document.querySelector('input[name="__RequestVerificationToken"]');
        if (!tokenElement) {
            console.error('Antiforgery token not found');
            showError('Antiforgery token not found. Please refresh the page.');
            return;
        }
        window.antiforgeryToken = tokenElement.value;

        // Load data
        await loadTargetsAndActuals();

        // Attach event handlers (scoped to targets-page)
        attachEventHandlers();
    });

    // ========== Data Loading ==========
    async function loadTargetsAndActuals() {
        try {
            // Fetch current targets
            const targetsResponse = await fetch(API_CURRENT);
            if (!targetsResponse.ok) {
                throw new Error(`Failed to load targets: ${targetsResponse.status}`);
            }
            const targets = await targetsResponse.json();

            // Store targets by contentTypeId
            targets.forEach(t => {
                state.currentTargets[t.contentTypeId] = t;
                state.lastSavedValues[t.contentTypeId] = {
                    targetUpload: t.targetUpload,
                    targetViews: t.targetViews
                };
            });

            // Fetch actuals for each content type
            for (const target of targets) {
                try {
                    // Call new GetActualAsync endpoint (via GET /targets/actual?contentTypeId=X)
                    const actualResponse = await fetch(`/targets/actual?contentTypeId=${target.contentTypeId}`);
                    if (actualResponse.ok) {
                        const actual = await actualResponse.json();
                        state.actuals[target.contentTypeId] = actual;
                    }
                } catch (err) {
                    console.warn(`Failed to load actual for contentTypeId ${target.contentTypeId}:`, err);
                    // Continue anyway - UI will show zero actuals or placeholder
                }
            }

            // Render tables
            renderTables(targets);
        } catch (error) {
            console.error('Error loading targets and actuals:', error);
            showError('Gagal memuat data. Silakan refresh halaman.');
        }
    }

    // ========== Rendering ==========
    function renderTables(targets) {
        const uploadBody = document.getElementById('uploadTableBody');
        const viewsBody = document.getElementById('viewsTableBody');

        uploadBody.innerHTML = '';
        viewsBody.innerHTML = '';

        targets.forEach(target => {
            const actual = state.actuals[target.contentTypeId] || { actualUpload: 0, actualViews: 0 };

            // Upload row
            const uploadRow = renderRow(
                target,
                actual.actualUpload,
                'upload',
                target.targetUpload,
                actual.actualUpload
            );
            uploadBody.appendChild(uploadRow);

            // Views row
            const viewsRow = renderRow(
                target,
                actual.actualViews,
                'views',
                target.targetViews,
                actual.actualViews
            );
            viewsBody.appendChild(viewsRow);
        });
    }

    function renderRow(target, actualValue, metric, targetValue, actualForDisplay) {
        const row = document.createElement('tr');
        row.setAttribute('data-content-type-id', target.contentTypeId);
        row.setAttribute('data-metric', metric);

        const selisih = actualForDisplay - targetValue;
        const status = calculateStatus(selisih);

        // Show empty input if target is 0 (no target set yet)
        const inputValue = targetValue > 0 ? targetValue : '';

        row.innerHTML = `
            <td class="type-cell">
                <span class="type-badge ${target.contentTypeCode === 'NON_KK' ? 'non-kk' : 'kk'}"></span>
                ${escapeHtml(target.contentTypeName)}
            </td>
            <td>
                <input 
                    type="number" 
                    class="target-input" 
                    data-field="${metric === 'upload' ? 'targetUpload' : 'targetViews'}"
                    value="${inputValue}"
                    placeholder="0"
                    min="0"
                    data-last-saved="${targetValue}"
                />
            </td>
            <td class="actual-cell">${formatNumber(actualForDisplay)}</td>
            <td class="selisih-cell ${selisih > 0 ? 'positive' : selisih < 0 ? 'negative' : 'zero'}">
                ${selisih > 0 ? '+' : ''}${formatNumber(selisih)}
            </td>
            <td>
                <span class="status-badge ${STATUS_CLASSES[status]}">
                    ${STATUS_LABELS[status]}
                </span>
            </td>
        `;

        const input = row.querySelector('.target-input');
        attachInputHandlers(input, target.contentTypeId, metric);

        return row;
    }

    // ========== Event Handlers ==========
    function attachEventHandlers() {
        const targetsPage = document.querySelector('.targets-page');
        if (!targetsPage) return;

        // Note: Event handlers are scoped via event delegation to inputs within .targets-page only
        // This ensures we don't accidentally capture inputs from other pages.
    }

    function attachInputHandlers(input, contentTypeId, metric) {
        let saveTimer = null;

        input.addEventListener('blur', function(e) {
            const newValue = parseInt(this.value) || 0;
            const lastSaved = parseInt(this.getAttribute('data-last-saved')) || 0;

            if (newValue === lastSaved) {
                console.log(`No change detected for ${metric} (${contentTypeId}), skipping save`);
                return;
            }

            // Validate: non-negative
            if (newValue < 0) {
                this.value = lastSaved;
                showError(`Target tidak boleh negatif.`);
                return;
            }

            triggerSave(contentTypeId, metric, newValue, input);
        });

        input.addEventListener('keypress', function(e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                const newValue = parseInt(this.value) || 0;
                const lastSaved = parseInt(this.getAttribute('data-last-saved')) || 0;

                if (newValue === lastSaved) {
                    console.log(`No change detected for ${metric} (${contentTypeId}), skipping save`);
                    this.blur();
                    return;
                }

                if (newValue < 0) {
                    this.value = lastSaved;
                    showError(`Target tidak boleh negatif.`);
                    return;
                }

                triggerSave(contentTypeId, metric, newValue, input);
                this.blur();
            }
        });

        input.addEventListener('input', function(e) {
            // Update Selisih and Status client-side in real-time
            updateRowCalculations(contentTypeId, metric);
        });
    }

    function updateRowCalculations(contentTypeId, metric) {
        const target = state.currentTargets[contentTypeId];
        const actual = state.actuals[contentTypeId] || { actualUpload: 0, actualViews: 0 };

        if (!target) return;

        // Find the row for this metric
        const rows = document.querySelectorAll(`tr[data-content-type-id="${contentTypeId}"][data-metric="${metric}"]`);
        if (rows.length === 0) return;

        const row = rows[0];
        const input = row.querySelector('.target-input');
        const targetValue = parseInt(input.value) || 0;
        const actualValue = metric === 'upload' ? actual.actualUpload : actual.actualViews;
        const selisih = actualValue - targetValue;
        const status = calculateStatus(selisih);

        // Update Selisih cell
        const selisihCell = row.querySelector('.selisih-cell');
        selisihCell.textContent = `${selisih > 0 ? '+' : ''}${formatNumber(selisih)}`;
        selisihCell.className = `selisih-cell ${selisih > 0 ? 'positive' : selisih < 0 ? 'negative' : 'zero'}`;

        // Update Status badge
        const statusBadge = row.querySelector('.status-badge');
        statusBadge.className = `status-badge ${STATUS_CLASSES[status]}`;
        statusBadge.textContent = STATUS_LABELS[status];
    }

    function calculateStatus(selisih) {
        if (selisih < 0) return 'below';
        if (selisih === 0) return 'met';
        return 'above';
    }

    // ========== Save Logic ==========
    async function triggerSave(contentTypeId, metric, newValue, inputElement) {
        const target = state.currentTargets[contentTypeId];
        if (!target) return;

        // Build payload
        const payload = {
            contentTypeId: contentTypeId,
            targetUpload: metric === 'upload' ? newValue : target.targetUpload,
            targetViews: metric === 'views' ? newValue : target.targetViews
        };

        // Show saving state
        inputElement.classList.add('input-saving');

        try {
            const response = await fetch(API_SAVE, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': window.antiforgeryToken || ''
                },
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                const errorData = await response.json();
                throw new Error(errorData.error || `Server error: ${response.status}`);
            }

            const result = await response.json();

            if (!result.success) {
                throw new Error(result.error || 'Save failed');
            }

            // Update local state
            state.currentTargets[contentTypeId] = {
                ...target,
                targetUpload: result.targetUpload,
                targetViews: result.targetViews
            };
            state.lastSavedValues[contentTypeId] = {
                targetUpload: result.targetUpload,
                targetViews: result.targetViews
            };

            // Update input's data-last-saved
            inputElement.setAttribute('data-last-saved', newValue);

            // Show success toast
            showToast('Tersimpan');

            // Update calculations
            updateRowCalculations(contentTypeId, metric);

        } catch (error) {
            console.error('Save error:', error);
            inputElement.classList.remove('input-saving');
            showError(`Gagal menyimpan: ${error.message}`);
        } finally {
            inputElement.classList.remove('input-saving');
        }
    }

    // ========== Toast & Error Handling ==========
    function showToast(message) {
        const toast = document.getElementById('saveToast');
        if (!toast) return;

        toast.textContent = message;
        toast.classList.add('show');

        setTimeout(() => {
            toast.classList.remove('show');
        }, TOAST_DURATION_MS);
    }

    function showError(message) {
        console.error('Error:', message);
        // For now, use browser alert or a simple notification
        // Could be enhanced with a dedicated error toast later
        alert(message);
    }

    // ========== Utilities ==========
    function formatNumber(value) {
        if (typeof value !== 'number') return '0';
        // Format with thousands separator (Indonesian: . for thousands, , for decimals)
        // For simplicity, use toLocaleString('id-ID')
        return value.toLocaleString('id-ID');
    }

    function escapeHtml(text) {
        const map = {
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#039;'
        };
        return text.replace(/[&<>"']/g, m => map[m]);
    }

})();
