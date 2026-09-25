/**
 * Shared calendar popup component.
 *
 * Reused by:
 *  - Daily Summary date range picker (createRangeCalendar — renders into the existing panel)
 *  - Targets "Berlaku Mulai" single date picker (createSingleDatePicker — self-contained popup)
 *
 * Visual language mirrors the Daily Summary date-picker panel; see wwwroot/css/calendar-popup.css.
 * All values are ISO yyyy-MM-dd strings. Selection comparisons use Date.getTime() exactly like
 * the original implementations (string dates parse consistently on both sides, so behaviour is
 * unchanged). Business semantics (what a page does with the chosen date) stay in the pages.
 */
(function () {
    'use strict';

    const MONTH_NAMES = ['Januari', 'Februari', 'Maret', 'April', 'Mei', 'Juni', 'Juli', 'Agustus', 'September', 'Oktober', 'November', 'Desember'];
    const WEEKDAYS = ['Sn', 'Sl', 'Rb', 'Km', 'Jm', 'Sb', 'Mg'];

    const parseIso = value => new Date(`${value}T00:00:00`);
    const isoDate = date => {
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const day = String(date.getDate()).padStart(2, '0');
        return `${year}-${month}-${day}`;
    };
    const firstOfMonth = value => {
        const d = value instanceof Date ? new Date(value) : parseIso(value);
        d.setDate(1);
        return d;
    };

    /**
     * Renders one month using the exact markup/styling of the Daily Summary calendar.
     * selection: { start: 'yyyy-MM-dd'|null, end: 'yyyy-MM-dd'|null }
     */
    function renderMonthHtml(month, selection) {
        const firstDay = new Date(month.getFullYear(), month.getMonth(), 1);
        const daysInMonth = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate();
        let html = `<div class="calendar-month"><strong>${firstDay.toLocaleDateString('id-ID', { month: 'long', year: 'numeric' })}</strong><div class="weekday-row">${WEEKDAYS.map(x => `<span>${x}</span>`).join('')}</div><div class="days-grid">`;
        const offset = (firstDay.getDay() + 6) % 7;
        for (let i = 0; i < offset; i++) html += '<span class="empty-day"></span>';
        for (let day = 1; day <= daysInMonth; day++) {
            const value = isoDate(new Date(month.getFullYear(), month.getMonth(), day));
            const selected = selection && ((selection.start && value === selection.start) || (selection.end && value === selection.end));
            const inRange = selection && selection.start && selection.end &&
                (new Date(value).getTime() > new Date(selection.start).getTime() && new Date(value).getTime() < new Date(selection.end).getTime());
            html += `<button type="button" class="calendar-day${selected ? ' selected' : ''}${inRange ? ' in-range' : ''}" data-date="${value}">${day}</button>`;
        }
        return html + '</div></div>';
    }

    /**
     * Range selection rules (identical to the original Daily Summary behaviour):
     *  1. no start yet       -> set start
     *  2. start without end  -> same/after day closes the range, earlier day restarts
     *  3. complete range     -> restart with a new start
     */
    function computeRangeSelection(current, iso) {
        const selTime = new Date(iso).getTime();
        if (!current.start) return { start: iso, end: null };
        if (!current.end) {
            const startTime = new Date(current.start).getTime();
            if (selTime >= startTime) return { start: current.start, end: iso };
            return { start: iso, end: null };
        }
        return { start: iso, end: null };
    }

    /**
     * Range calendar that renders into an existing panel. The host page keeps ownership of
     * its panel chrome (presets, Apply/Cancel) and of the selection state: it is read via
     * getSelection() on every render and updated through onSelectionChange().
     */
    function createRangeCalendar(options) {
        const grid = options.grid;
        const titleEl = options.titleEl;
        const monthCount = options.months || 2;
        let anchor = firstOfMonth(options.initialMonth || new Date());

        function render() {
            const selection = options.getSelection ? options.getSelection() : { start: null, end: null };
            const months = [];
            for (let i = 0; i < monthCount; i++) {
                const m = new Date(anchor);
                m.setMonth(m.getMonth() + i);
                months.push(m);
            }
            if (titleEl) titleEl.textContent = months.map(m => `${MONTH_NAMES[m.getMonth()]} ${m.getFullYear()}`).join(' · ');
            grid.innerHTML = months.map(m => renderMonthHtml(m, selection)).join('');
            grid.querySelectorAll('[data-date]').forEach(day => day.addEventListener('click', event => {
                // The click re-renders the calendar grid, detaching this button from the DOM before
                // the event finishes bubbling; stop it here so the click-outside handler cannot close the panel.
                event.stopPropagation();
                const next = computeRangeSelection(selection, day.dataset.date);
                if (options.onSelectionChange) options.onSelectionChange(next.start, next.end);
                render();
            }));
        }

        if (options.prevButton) options.prevButton.addEventListener('click', () => { anchor.setMonth(anchor.getMonth() - 1); render(); });
        if (options.nextButton) options.nextButton.addEventListener('click', () => { anchor.setMonth(anchor.getMonth() + 1); render(); });

        return {
            render,
            setMonth: value => { anchor = firstOfMonth(value); }
        };
    }

    /**
     * Self-contained single-date popup anchored to a trigger element.
     * Interaction: click a day = select + close (with a Hapus button to clear the value).
     * The host is notified via onSelect(iso)/onClear() and stays owner of the stored value.
     */
    function createSingleDatePicker(options) {
        const trigger = options.trigger;
        const anchorEl = options.anchor || trigger.parentElement;
        let value = options.initial || null;
        let monthAnchor = firstOfMonth(value ? parseIso(value) : new Date());

        const panel = document.createElement('section');
        panel.className = 'calendar-popup-panel';
        panel.hidden = true;
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', options.ariaLabel || 'Pilih tanggal');
        panel.innerHTML = `
            <div class="calendar-toolbar">
                <button type="button" data-nav="prev" aria-label="Previous month">‹</button>
                <strong class="calendar-popup-title"></strong>
                <button type="button" data-nav="next" aria-label="Next month">›</button>
            </div>
            <div class="calendar-grid is-single"></div>
            <div class="calendar-popup-actions">
                <span class="calendar-popup-selected"></span>
                <button type="button" class="calendar-popup-clear" hidden>Hapus</button>
            </div>`;
        anchorEl.appendChild(panel);

        const grid = panel.querySelector('.calendar-grid');
        const titleEl = panel.querySelector('.calendar-popup-title');
        const selectedLabel = panel.querySelector('.calendar-popup-selected');
        const clearButton = panel.querySelector('.calendar-popup-clear');

        function updateFooter() {
            selectedLabel.textContent = value
                ? parseIso(value).toLocaleDateString('id-ID', { day: '2-digit', month: 'short', year: 'numeric' })
                : 'Belum dipilih';
            clearButton.hidden = !value;
        }

        function render() {
            titleEl.textContent = `${MONTH_NAMES[monthAnchor.getMonth()]} ${monthAnchor.getFullYear()}`;
            grid.innerHTML = renderMonthHtml(monthAnchor, { start: value, end: null });
            grid.querySelectorAll('[data-date]').forEach(day => day.addEventListener('click', event => {
                event.stopPropagation();
                value = day.dataset.date;
                updateFooter();
                if (options.onSelect) options.onSelect(value);
                close();
            }));
        }

        function position() {
            // Open below-left of the trigger by default; flip to the right edge when the
            // popup would overflow the viewport (small windows / mobile).
            panel.style.left = '0px';
            panel.style.right = 'auto';
            const rect = panel.getBoundingClientRect();
            const viewportRight = document.documentElement.clientWidth;
            if (rect.right > viewportRight - 8) {
                panel.style.left = 'auto';
                panel.style.right = '0px';
            }
        }

        function open() {
            monthAnchor = firstOfMonth(value ? parseIso(value) : new Date());
            updateFooter();
            render();
            panel.hidden = false;
            trigger.setAttribute('aria-expanded', 'true');
            position();
        }

        function close() {
            panel.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
        }

        function onDocumentClick(event) {
            if (panel.hidden) return;
            const target = event.target instanceof Element ? event.target : null;
            if (!target) return;
            if (!panel.contains(target) && !trigger.contains(target)) close();
        }

        function onDocumentKeydown(event) {
            if (event.key === 'Escape' && !panel.hidden) close();
        }

        trigger.addEventListener('click', () => { panel.hidden ? open() : close(); });
        clearButton.addEventListener('click', event => {
            event.stopPropagation();
            value = null;
            if (options.onClear) options.onClear();
            close();
        });
        panel.querySelector('[data-nav="prev"]').addEventListener('click', () => { monthAnchor.setMonth(monthAnchor.getMonth() - 1); render(); });
        panel.querySelector('[data-nav="next"]').addEventListener('click', () => { monthAnchor.setMonth(monthAnchor.getMonth() + 1); render(); });
        document.addEventListener('click', onDocumentClick);
        document.addEventListener('keydown', onDocumentKeydown);

        return {
            open,
            close,
            isOpen: () => !panel.hidden,
            getValue: () => value,
            setValue: iso => { value = iso || null; updateFooter(); },
            panel
        };
    }

    window.CalendarPopup = { createRangeCalendar, createSingleDatePicker };
})();
