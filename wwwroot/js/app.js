// ── SIDEBAR MOBILE ─────────────────────────────
const sidebar   = document.getElementById('sidebar');
const backdrop  = document.getElementById('sidebarBackdrop');
const toggleBtn = document.getElementById('sidebarToggle');

toggleBtn?.addEventListener('click', () => {
    sidebar.classList.toggle('open');
    backdrop.classList.toggle('show');
});
backdrop?.addEventListener('click', () => {
    sidebar.classList.remove('open');
    backdrop.classList.remove('show');
});

// ── TOAST AUTO-HIDE ────────────────────────────
const toast = document.getElementById('toastMsg');
if (toast) {
    setTimeout(() => toast.classList.remove('show'), 4500);
}

// ── NOTIFICATIONS ──────────────────────────────
async function carregarNotificacoes() {
    try {
        const res  = await fetch('/Notificacoes/Recentes');
        const data = await res.json();
        const badge = document.getElementById('notifCount');
        const list  = document.getElementById('notifList');

        if (data.length > 0) {
            badge?.classList.remove('d-none');
            list && (list.innerHTML = data.map(n => `
                <div class="notif-item unread" onclick="marcarLida(${n.id})">
                    <div class="notif-item-icon"><i class="${n.iconeCss}"></i></div>
                    <div class="notif-body">
                        <div class="notif-title">${n.titulo}</div>
                        <div class="notif-msg">${n.mensagem}</div>
                    </div>
                </div>`).join(''));
        } else {
            badge?.classList.add('d-none');
        }
    } catch {}
}

async function marcarLida(id) {
    await fetch(`/Notificacoes/Marcar/${id}`, { method: 'POST', headers: { 'RequestVerificationToken': getToken() } });
}

async function marcarTodasLidas() {
    await fetch('/Notificacoes/MarcarTodas', { method: 'POST', headers: { 'RequestVerificationToken': getToken() } });
    document.getElementById('notifCount')?.classList.add('d-none');
    document.getElementById('notifList').innerHTML = `
        <div class="notif-empty">
            <i class="bi bi-bell-slash fs-3 text-muted d-block mb-2"></i>
            <span class="text-muted small">Sem notificações</span>
        </div>`;
}

function getToken() {
    return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
}

if (document.getElementById('notifBtn')) {
    carregarNotificacoes();
    setInterval(carregarNotificacoes, 60000);
}

// ── FINANCEIRO: MARCAR PAGO ────────────────────
document.querySelectorAll('[data-pago-toggle]').forEach(btn => {
    btn.addEventListener('click', async () => {
        const id  = btn.dataset.pagoToggle;
        const res = await fetch(`/Financeiro/MarcarPago/${id}`, {
            method: 'POST', headers: { 'RequestVerificationToken': getToken() }
        });
        if (res.ok) {
            const data = await res.json();
            btn.className = `btn btn-sm ${data.pago ? 'btn-success' : 'btn-outline-secondary'}`;
            btn.style.minWidth = '100px';
            btn.style.fontSize = '12px';
            btn.innerHTML = data.pago
                ? '<i class="bi bi-check-circle-fill me-1"></i>Pago'
                : '<i class="bi bi-circle me-1"></i>Pendente';
        }
    });
});
