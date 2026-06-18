'use strict';
/* ═══════════════════════════════════════════════════════
   IECC ADMIN – SHARED JS
   ═══════════════════════════════════════════════════════ */

const SIDEBAR = `<aside class="as" id="admin-sidebar">
  <div class="sb-brand">
    <div class="sb-brand-icon">🕌</div>
    <div><span class="sb-brand-name">IECC Admin</span><span class="sb-brand-sub">MANAGEMENT DASHBOARD</span></div>
  </div>
  <nav class="sb-nav">
    <div class="sb-sec">Management</div>
    <a class="sb-link" href="/admin/dashboard.html" data-p="dashboard"><span class="sb-link-ic">📊</span> Dashboard</a>
    <a class="sb-link" href="/admin/donations.html" data-p="donations"><span class="sb-link-ic">💰</span> Donations</a>
    <a class="sb-link" href="/admin/contacts.html"  data-p="contacts"><span class="sb-link-ic">📧</span> Contacts</a>
    <a class="sb-link" href="/admin/volunteers.html" data-p="volunteers"><span class="sb-link-ic">🤝</span> Volunteers</a>
    <div class="sb-sec">Content</div>
    <a class="sb-link" href="/admin/events.html"     data-p="events"><span class="sb-link-ic">📅</span> Events</a>
    <a class="sb-link" href="/admin/newsletter.html" data-p="newsletter"><span class="sb-link-ic">📨</span> Newsletter</a>
    <div class="sb-sec">System</div>
    <a class="sb-link" href="/admin/settings.html" data-p="settings"><span class="sb-link-ic">⚙️</span> Settings</a>
    <div class="sb-sec">Site</div>
    <a class="sb-link" href="/" target="_blank"><span class="sb-link-ic">🌐</span> View Site ↗</a>
    <button class="sb-link" onclick="logout()"><span class="sb-link-ic">🚪</span> Logout</button>
  </nav>
  <div class="sb-foot" id="sb-user"></div>
</aside>`;

/* ── Auth ─────────────────────────────────────────────── */
async function requireAuth() {
  const ph = document.getElementById('sb-ph');
  if (ph) ph.outerHTML = SIDEBAR;
  try {
    const res = await fetch('/admin/api/me');
    if (!res.ok) { location.href = '/admin/'; return null; }
    const u = await res.json();
    const el = document.getElementById('sb-user');
    if (el) el.textContent = '👤 ' + u.username;
    const nu = document.getElementById('admin-username');
    if (nu) nu.textContent = u.username;
    const page = location.pathname.split('/').pop().replace('.html','');
    document.querySelectorAll('.sb-link[data-p]').forEach(a => {
      if (a.dataset.p === page) a.classList.add('active');
    });
    return u;
  } catch { location.href = '/admin/'; return null; }
}

async function logout() {
  await fetch('/admin/logout', { method:'POST' });
  location.href = '/admin/';
}

/* ── API helpers ──────────────────────────────────────── */
async function api(method, url, body) {
  try {
    const opts = { method, headers:{} };
    if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
    const res = await fetch(url, opts);
    if (res.status === 401) { location.href = '/admin/'; return null; }
    if (!res.ok) { adminToast('Request failed', 'error'); return null; }
    const ct = res.headers.get('content-type') || '';
    return ct.includes('json') ? res.json() : res.text();
  } catch { adminToast('Network error', 'error'); return null; }
}
const apiGet    = u       => api('GET',    u);
const apiPost   = (u,b)   => api('POST',   u, b);
const apiPatch  = (u,b)   => api('PATCH',  u, b ?? {});
const apiDelete = u       => api('DELETE', u);
const apiPut    = (u,b)   => api('PUT',    u, b);

/* ── Toast ────────────────────────────────────────────── */
function adminToast(msg, type='success') {
  const el = document.createElement('div');
  el.className = `atoast ${type}`;
  el.textContent = msg;
  document.body.appendChild(el);
  requestAnimationFrame(() => el.classList.add('show'));
  setTimeout(() => { el.classList.remove('show'); setTimeout(() => el.remove(), 300); }, 3000);
}

/* ── Formatters ───────────────────────────────────────── */
function fmtDate(d) {
  if (!d) return '—';
  return new Date(d).toLocaleDateString('en-US', { month:'short', day:'numeric', year:'numeric' });
}
function fmtAmt(n) {
  return '$' + Number(n||0).toLocaleString('en-US',{minimumFractionDigits:2, maximumFractionDigits:2});
}
function badge(val, cls) {
  const c = (cls || val || '').toString().toLowerCase().replace(/\s+/g,'-').replace(/[^a-z0-9-]/g,'');
  return `<span class="badge badge-${c}">${esc(val)}</span>`;
}
function esc(s) {
  if (s === null || s === undefined) return '—';
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
function fmtTime(t) {
  if (!t) return '';
  const [h,m] = t.split(':');
  const hh = parseInt(h), ap = hh < 12 ? 'AM' : 'PM';
  return `${hh===0?12:hh>12?hh-12:hh}:${m} ${ap}`;
}

/* ── CSV export ───────────────────────────────────────── */
function exportCSV(data, filename) {
  if (!data?.length) { adminToast('No data to export','error'); return; }
  const keys = Object.keys(data[0]);
  const rows = [keys.join(','), ...data.map(r => keys.map(k => `"${(r[k]??'').toString().replace(/"/g,'""')}"`).join(','))];
  const blob = new Blob([rows.join('\n')], { type:'text/csv' });
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = filename; a.click();
}

/* ── Modal helpers ────────────────────────────────────── */
function openModal(id) { document.getElementById(id).classList.add('open'); }
function closeModal(id) { document.getElementById(id).classList.remove('open'); }
