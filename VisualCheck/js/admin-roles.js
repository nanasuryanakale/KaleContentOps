/**
 * Master Role & Permission admin screen (Admin Management MVP).
 * Tracks checkbox changes across all role cards, then submits ONE delta request
 * per changed role to /Admin/Roles/SetPermissions (antiforgery header included).
 */
(function () {
    'use strict';

    const saveBar = document.getElementById('saveBar');
    if (!saveBar) return; // page rendered without manage capability

    const saveBtn = document.getElementById('savePermChanges');
    const discardBtn = document.getElementById('discardPermChanges');
    const roleLists = Array.from(document.querySelectorAll('.role-perm-list'));
    const savedStates = new Map(); // roleId -> Set of saved permission values

    function currentSet(list) {
        return new Set(Array.from(list.querySelectorAll('input[type="checkbox"]:checked')).map(cb => cb.value));
    }

    function listByRole(roleId) {
        return roleLists.find(list => list.dataset.roleId === roleId);
    }

    function changedRoles() {
        return roleLists
            .filter(list => {
                const saved = savedStates.get(list.dataset.roleId);
                if (!saved) return false;
                const now = currentSet(list);
                if (now.size !== saved.size) return true;
                for (const value of now) {
                    if (!saved.has(value)) return true;
                }
                return false;
            })
            .map(list => list.dataset.roleId);
    }

    function refreshBar() {
        saveBar.hidden = changedRoles().length === 0;
    }

    roleLists.forEach(list => {
        savedStates.set(list.dataset.roleId, currentSet(list));
        list.addEventListener('change', refreshBar);
    });

    discardBtn.addEventListener('click', () => {
        changedRoles().forEach(roleId => {
            const list = listByRole(roleId);
            const saved = savedStates.get(roleId);
            list.querySelectorAll('input[type="checkbox"]').forEach(cb => {
                cb.checked = saved.has(cb.value);
            });
        });
        refreshBar();
    });

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

    saveBtn.addEventListener('click', async () => {
        saveBtn.disabled = true;
        try {
            const roleIds = changedRoles();
            let failures = [];
            for (const roleId of roleIds) {
                const list = listByRole(roleId);
                const result = await postJson('/Admin/Roles/SetPermissions', {
                    roleId: roleId,
                    permissions: Array.from(currentSet(list))
                });
                if (!result.success) {
                    failures.push(`${roleId}: ${(result.errors || []).join(' ')}`);
                } else {
                    savedStates.set(roleId, currentSet(list));
                }
            }

            if (failures.length > 0) {
                alert('Sebagian perubahan gagal:\n' + failures.join('\n'));
            }
            refreshBar();
        } finally {
            saveBtn.disabled = false;
        }
    });
})();
