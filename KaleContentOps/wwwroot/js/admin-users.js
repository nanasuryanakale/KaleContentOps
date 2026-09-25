/**
 * Master User admin screen (Admin Management MVP).
 * Talks to /Admin/Users/* JSON endpoints with the antiforgery header,
 * following the existing /targets/save client pattern.
 */
(function () {
    'use strict';

    const table = document.getElementById('usersTable');
    const modal = document.getElementById('userModal');
    if (!table || !modal) return; // view-only user: no manage UI rendered

    const title = document.getElementById('userModalTitle');
    const subtitle = document.getElementById('userModalSubtitle');
    const errorBox = document.getElementById('userModalError');
    const fUserName = document.getElementById('fUserName');
    const fDisplayName = document.getElementById('fDisplayName');
    const fEmail = document.getElementById('fEmail');
    const fRole = document.getElementById('fRole');
    const fPassword = document.getElementById('fPassword');
    const fNewPassword = document.getElementById('fNewPassword');
    const passwordField = document.getElementById('passwordField');
    const newPasswordField = document.getElementById('newPasswordField');
    const fActive = document.getElementById('fActive');
    const submitBtn = document.getElementById('submitUserModal');
    // Page-level error box (outside the modal) for table actions such as
    // Nonaktifkan/Aktifkan - the modal error box is invisible while the modal is closed.
    const actionError = document.getElementById('userActionError');

    let mode = 'create';
    let editingUserId = null;

    function readRow(id) {
        const row = table.querySelector(`tr[data-user="${id}"]`);
        if (!row) return null;
        return {
            id: row.dataset.user,
            username: row.dataset.username,
            displayName: row.dataset.displayname,
            email: row.dataset.email === '' ? null : row.dataset.email,
            role: row.dataset.role,
            active: row.dataset.active === 'True'
        };
    }

    function openModal(newMode, userId) {
        mode = newMode;
        editingUserId = userId || null;
        errorBox.hidden = true;
        errorBox.textContent = '';

        if (mode === 'create') {
            title.textContent = 'User Baru';
            subtitle.textContent = 'Buat user internal dengan role dan password awal.';
            fUserName.value = '';
            fUserName.disabled = false;
            fDisplayName.value = '';
            fEmail.value = '';
            fRole.selectedIndex = 0;
            fPassword.value = '';
            fActive.checked = true;
            passwordField.hidden = false;
            newPasswordField.hidden = true;
        } else {
            const u = readRow(editingUserId);
            if (!u) return;
            title.textContent = `Edit User — ${u.username}`;
            subtitle.textContent = 'Display name, email, role, status, dan reset password.';
            fUserName.value = u.username;
            fUserName.disabled = true;
            fDisplayName.value = u.displayName || '';
            fEmail.value = u.email || '';
            fRole.value = u.role;
            if (fRole.value !== u.role) fRole.selectedIndex = 0; // safety when role missing
            fActive.checked = u.active;
            fPassword.value = '';
            fNewPassword.value = '';
            passwordField.hidden = true;
            newPasswordField.hidden = false;
        }
        modal.hidden = false;
    }

    function closeModal() {
        modal.hidden = true;
    }

    function showError(message) {
        errorBox.textContent = message;
        errorBox.hidden = false;
    }

    function showActionError(message) {
        if (!actionError) return; // view-only render never shows action buttons
        actionError.textContent = message;
        actionError.hidden = false;
        actionError.scrollIntoView({ block: 'nearest' });
    }

    async function postJson(url, payload) {
        const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
        const response = await fetch(url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': tokenInput ? tokenInput.value : ''
            },
            body: JSON.stringify(payload)
        });
        if (response.status === 403) {
            return { success: false, errors: ['403: Anda tidak memiliki izin untuk aksi ini.'] };
        }
        try {
            return await response.json();
        } catch {
            return { success: false, errors: [`HTTP ${response.status}`] };
        }
    }

    async function submit() {
        submitBtn.disabled = true;
        try {
            let result;
            if (mode === 'create') {
                result = await postJson('/Admin/Users/Create', {
                    userName: fUserName.value.trim(),
                    displayName: fDisplayName.value.trim() || null,
                    email: fEmail.value.trim() || null,
                    roleName: fRole.value,
                    password: fPassword.value,
                    isActive: fActive.checked
                });
            } else {
                result = await postJson('/Admin/Users/Update', {
                    userId: editingUserId,
                    displayName: fDisplayName.value.trim() || null,
                    email: fEmail.value.trim() || null,
                    roleName: fRole.value,
                    isActive: fActive.checked
                });
                // Optional admin password reset on edit (Identity reset-token flow server-side).
                if (result.success && fNewPassword.value) {
                    result = await postJson('/Admin/Users/SetPassword', {
                        userId: editingUserId,
                        newPassword: fNewPassword.value
                    });
                }
            }

            if (result.success) {
                window.location.reload();
            } else {
                const message = (result.errors || ['Gagal menyimpan']).join(' ');
                showError(message);       // in-modal feedback (existing pattern)
                showActionError(message); // visible even if the modal is closed (e.g. LAST_ADMIN rejection)
            }
        } finally {
            submitBtn.disabled = false;
        }
    }

    document.getElementById('btnNewUser').addEventListener('click', () => openModal('create'));
    document.getElementById('closeUserModal').addEventListener('click', closeModal);
    document.getElementById('cancelUserModal').addEventListener('click', closeModal);
    submitBtn.addEventListener('click', submit);

    table.addEventListener('click', event => {
        const button = event.target.closest('button');
        if (!button) return;
        if (button.classList.contains('js-edit') || button.classList.contains('js-password')) {
            openModal('edit', button.dataset.id);
        }
    });

    table.addEventListener('click', event => {
        const button = event.target.closest('button.js-deactivate, button.js-activate');
        if (!button) return;
        const isActive = button.classList.contains('js-activate');
        const label = isActive ? 'Aktifkan' : 'Nonaktifkan';
        if (!confirm(`${label} user ini?`)) return;
        postJson('/Admin/Users/SetActive', { userId: button.dataset.id, isActive }).then(result => {
            if (result.success) {
                window.location.reload();
            } else {
                // The modal is closed for table actions - surface the backend
                // rejection on the page itself so the user actually sees it
                // (e.g. LAST_ADMIN safeguard, row state stays unchanged).
                showActionError((result.errors || ['Gagal']).join(' '));
            }
        });
    });
})();
