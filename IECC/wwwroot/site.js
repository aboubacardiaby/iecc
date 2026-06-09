'use strict';
/* ═══════════════════════════════════════════════════════
   IECC MASJID – SITE.JS  (shared across all pages)
   ═══════════════════════════════════════════════════════ */

/* ── Prayer time calculation (ISNA, Crystal MN) ─────── */
const PT = { lat: 45.0322, lng: 93.3533 }; // lng positive = West

function _calcPT(date) {
  let y = date.getFullYear(), m = date.getMonth() + 1, d = date.getDate();
  if (m <= 2) { y--; m += 12; }
  const A = Math.floor(y/100), B = 2 - A + Math.floor(A/4);
  const JD = Math.floor(365.25*(y+4716)) + Math.floor(30.6001*(m+1)) + d + B - 1524.5;
  const T = (JD - 2451545.0) / 36525;
  const L0 = ((280.46646 + 36000.76983*T) % 360 + 360) % 360;
  const M0 = ((357.52911 + 35999.05029*T) % 360 + 360) % 360;
  const Mr = M0 * Math.PI/180;
  const C  = (1.914602 - 0.004817*T)*Math.sin(Mr) + 0.019993*Math.sin(2*Mr) + 0.000289*Math.sin(3*Mr);
  const SL = (L0+C) * Math.PI/180;
  const ep = (23.439291 - 0.013004*T) * Math.PI/180;
  const dec = Math.asin(Math.sin(ep)*Math.sin(SL));
  const L0r = L0*Math.PI/180, e = 0.016708634;
  const y2  = Math.pow(Math.tan(ep/2), 2);
  const EqT = 4*(180/Math.PI)*(y2*Math.sin(2*L0r) - 2*e*Math.sin(Mr) + 4*e*y2*Math.sin(Mr)*Math.cos(2*L0r) - 0.5*y2*y2*Math.sin(4*L0r) - 1.25*e*e*Math.sin(2*Mr));
  const utcOff = -date.getTimezoneOffset()/60;
  const noon = 12 + PT.lng/15 - EqT/60 + utcOff;
  const latR = PT.lat * Math.PI/180;
  function ha(elev) {
    const c = (Math.sin(elev*Math.PI/180) - Math.sin(latR)*Math.sin(dec)) / (Math.cos(latR)*Math.cos(dec));
    return (c < -1 || c > 1) ? null : (180/Math.PI)*Math.acos(c)/15;
  }
  const sunHA  = ha(-0.8333);
  const fajrHA = ha(-15);
  const ishaHA = ha(-15);
  const asrElev = (180/Math.PI)*Math.atan(1/(1+Math.tan(Math.abs(latR - dec))));
  const asrHA  = ha(asrElev);
  const w = h => h === null ? null : ((h%24)+24)%24;
  return {
    fajr:    w(fajrHA !== null ? noon - fajrHA : null),
    sunrise: w(sunHA  !== null ? noon - sunHA  : null),
    dhuhr:   w(noon),
    asr:     w(asrHA  !== null ? noon + asrHA  : null),
    maghrib: w(sunHA  !== null ? noon + sunHA  : null),
    isha:    w(ishaHA !== null ? noon + ishaHA : null)
  };
}

function _fmt(h, ampm = true) {
  if (h === null) return '--:--';
  const mins = Math.round(h*60), hh = Math.floor(mins/60)%24, mm = mins%60;
  const ap = hh < 12 ? 'AM' : 'PM', dh = hh === 0 ? 12 : hh > 12 ? hh-12 : hh;
  return ampm ? `${dh}:${String(mm).padStart(2,'0')} ${ap}` : `${dh}:${String(mm).padStart(2,'0')}`;
}

const PRAYERS = [
  { key:'fajr',    name:'Fajr',    icon:'🌙', note:'Pre-dawn prayer' },
  { key:'sunrise', name:'Sunrise', icon:'🌅', note:'Sun rises' },
  { key:'dhuhr',   name:'Dhuhr',   icon:'☀️',  note:'Midday prayer' },
  { key:'asr',     name:'Asr',     icon:'🌤',  note:'Afternoon prayer' },
  { key:'maghrib', name:'Maghrib', icon:'🌇',  note:'Sunset prayer' },
  { key:'isha',    name:'Isha',    icon:'🌃',  note:'Night prayer' }
];

function _nextPrayer(times) {
  const now = new Date(), h = now.getHours() + now.getMinutes()/60;
  for (const p of PRAYERS) if (times[p.key] !== null && times[p.key] > h) return p.key;
  return 'fajr';
}

/* ── Shared Logo SVG ──────────────────────────────────── */
const LOGO_SVG = `<svg viewBox="0 0 60 60" width="36" height="36" fill="none" xmlns="http://www.w3.org/2000/svg">
  <circle cx="30" cy="30" r="28" fill="#1a3d28" stroke="#e8a020" stroke-width="2"/>
  <path d="M18 34 Q30 16 42 34" fill="#e8a020"/>
  <rect x="14" y="26" width="5" height="10" fill="#e8a020" rx="1"/>
  <rect x="15.5" y="22" width="2" height="5" fill="#e8a020"/>
  <rect x="41" y="26" width="5" height="10" fill="#e8a020" rx="1"/>
  <rect x="42.5" y="22" width="2" height="5" fill="#e8a020"/>
  <rect x="16" y="34" width="28" height="8" fill="#e8a020" rx="1"/>
  <path d="M27 42 L27 37 Q30 34 33 37 L33 42 Z" fill="#1a3d2a"/>
  <path d="M28.5 20 Q32 18 35 21 Q31 19 28.5 22 Z" fill="#fff"/>
  <circle cx="34" cy="14" r="1.5" fill="#fff"/>
</svg>`;

/* ── Nav HTML ─────────────────────────────────────────── */
const NAV_HTML = `<nav class="site-nav" id="site-nav">
  <div class="nav-container">
    <a href="/" class="nav-brand">${LOGO_SVG}<div class="brand-text"><span class="brand-name">IECC</span><span class="brand-tag">MASJID</span></div></a>
    <button class="nav-toggle" id="nav-toggle" aria-label="Menu"><span></span><span></span><span></span></button>
    <ul class="nav-menu" id="nav-menu">
      <li><a href="/"                  data-href="index">Home</a></li>
      <li><a href="/about.html"        data-href="about">About</a></li>
      <li><a href="/bylaws.html"       data-href="bylaws">By-Laws</a></li>
      <li><a href="/prayer-times.html" data-href="prayer-times">Prayer Times</a></li>
      <li><a href="/programs.html"     data-href="programs">Programs</a></li>
      <li><a href="/events.html"       data-href="events">Events</a></li>
      <li><a href="/donate.html"       data-href="donate">Donate</a></li>
      <li><a href="/volunteer.html"    data-href="volunteer">Volunteer</a></li>
      <li><a href="/contact.html"      data-href="contact">Contact</a></li>
      <li class="nav-cta-li"><button class="nav-cta-btn" onclick="openModal()">&#9829; Donate Now</button></li>
    </ul>
  </div>
</nav>`;

/* ── Footer HTML ──────────────────────────────────────── */
const FOOTER_HTML = `<footer class="site-footer">
  <div class="footer-grid">
    <div class="footer-brand">
      <div class="footer-brand-logo">${LOGO_SVG}<div><span class="footer-brand-name">IECC Masjid</span><span class="footer-brand-sub">ISLAMIC EDUCATION &amp; CULTURAL CENTER</span></div></div>
      <p>Serving the Crystal, MN Muslim community with faith, knowledge, and service. Building a lasting home for generations to come.</p>
      <div class="footer-social">
        <a href="#" aria-label="Facebook">f</a>
        <a href="#" aria-label="Instagram">ig</a>
        <a href="#" aria-label="YouTube">yt</a>
      </div>
    </div>
    <div>
      <p class="footer-col-title">Quick Links</p>
      <ul class="footer-links">
        <li><a href="/">Home</a></li>
        <li><a href="/about.html">About Us</a></li>
        <li><a href="/bylaws.html">By-Laws</a></li>
        <li><a href="/prayer-times.html">Prayer Times</a></li>
        <li><a href="/programs.html">Programs</a></li>
      </ul>
    </div>
    <div>
      <p class="footer-col-title">Community</p>
      <ul class="footer-links">
        <li><a href="/events.html">Events</a></li>
        <li><a href="/donate.html">Donate</a></li>
        <li><a href="/volunteer.html">Volunteer</a></li>
        <li><a href="/contact.html">Contact</a></li>
      </ul>
    </div>
    <div>
      <p class="footer-col-title">Contact Us</p>
      <div class="footer-ci">📍<span>4801 Welcome Ave North<br>Crystal, MN 55429</span></div>
      <div class="footer-ci">📞<span>612-985-2768</span></div>
      <div class="footer-ci">📧<span>iecc.masjid@gmail.com</span></div>
      <div class="footer-ci">🕐<span>Open for all 5 daily prayers</span></div>
    </div>
  </div>
  <div class="footer-bottom">
    <p class="footer-copy">&copy; 2026 IECC Masjid &nbsp;&middot;&nbsp; 501(c)(3) Nonprofit &nbsp;&middot;&nbsp; All donations tax-deductible &nbsp;&middot;&nbsp; Crystal, MN</p>
    <p class="footer-copy">Designed with &#9829; for our community</p>
  </div>
</footer>`;

/* ── Modal HTML (injected so it's available on all pages) */
const MODAL_HTML = `
<div class="modal-backdrop" id="modal-backdrop" onclick="handleBackdropClick(event)">
  <div class="modal" role="dialog" aria-modal="true" aria-labelledby="modal-title">
    <div class="modal-header">
      <div class="modal-title-block"><span class="modal-heart">&#9829;</span><div><h2 id="modal-title">Make Your Donation</h2><p class="modal-org">IECC Masjid &middot; Crystal, MN</p></div></div>
      <button class="modal-close" onclick="closeModal()" aria-label="Close">&times;</button>
    </div>
    <div class="modal-section">
      <p class="modal-label">Giving Frequency</p>
      <div class="freq-toggle">
        <button class="freq-btn active" data-freq="one-time" onclick="setFreq(this)">One Time</button>
        <button class="freq-btn" data-freq="monthly" onclick="setFreq(this)">Monthly</button>
      </div>
    </div>
    <div class="modal-section">
      <p class="modal-label">Select Amount</p>
      <div class="modal-amounts" id="modal-amounts"></div>
      <div class="modal-custom"><span class="custom-dollar">$</span><input type="number" id="custom-amt" placeholder="Other amount (min $100)" min="100" oninput="handleCustomAmt(this)"/></div>
    </div>
    <div class="modal-section">
      <p class="modal-label">Payment Method</p>
      <div class="pay-tabs">
        <button class="pay-tab active" data-tab="zelle"  onclick="setPayTab(this)">Zelle</button>
        <button class="pay-tab"        data-tab="paypal" onclick="setPayTab(this)">PayPal</button>
        <button class="pay-tab"        data-tab="card"   onclick="setPayTab(this)">Card</button>
      </div>
      <div class="pay-panel" id="panel-zelle">
        <div class="pay-instruction"><div class="pay-instr-icon">&#128242;</div><div><p class="pay-instr-title">Send via Zelle to:</p><p class="pay-number">612-985-2768</p><p class="pay-note">Enrolled as: <strong>IECC Masjid</strong></p><p class="pay-note">Memo: <em>Masjid Donation</em></p></div><button class="instr-copy" data-copy="612-985-2768" onclick="copyText(this)">Copy</button></div>
        <div class="pay-steps"><p>1. Open your banking app &rarr; <strong>Zelle</strong></p><p>2. Send <strong id="zelle-amt-display">$100</strong> to <strong>612-985-2768</strong></p><p>3. Memo: <em>Masjid Donation</em></p></div>
      </div>
      <div class="pay-panel hidden" id="panel-paypal">
        <div class="paypal-panel-header"><span class="paypal-panel-title">PayPal Secure Checkout</span></div>
        <p class="paypal-panel-note">A PayPal window will open to complete your donation.</p>
        <div id="paypal-loading" class="paypal-loading-msg"><span class="paypal-spinner"></span> Loading PayPal&hellip;</div>
        <div id="paypal-button-container"></div>
      </div>
      <div class="pay-panel hidden" id="panel-card">
        <div class="card-form">
          <div class="card-field full"><label>Name on Card</label><input type="text" placeholder="Full name" autocomplete="cc-name"/></div>
          <div class="card-field full"><label>Card Number</label><div class="card-input-wrap"><input type="text" placeholder="1234 5678 9012 3456" maxlength="19" id="card-num" oninput="fmtCard(this)" autocomplete="cc-number"/><span class="card-brand" id="card-brand"></span></div></div>
          <div class="card-field half"><label>Expiry</label><input type="text" placeholder="MM / YY" maxlength="7" oninput="fmtExpiry(this)" autocomplete="cc-exp"/></div>
          <div class="card-field half"><label>CVV</label><input type="text" placeholder="123" maxlength="4" autocomplete="cc-csc"/></div>
          <div class="card-field full"><label>Email (receipt)</label><input type="email" placeholder="your@email.com" autocomplete="email"/></div>
        </div>
        <p class="card-secure">&#128274; Secured &amp; encrypted. Card info never stored.</p>
      </div>
    </div>
    <div class="modal-section" id="donor-info-section">
      <p class="modal-label">Your Information <span class="optional">(optional)</span></p>
      <div class="donor-fields">
        <input type="text"  class="donor-input" placeholder="Your name"/>
        <input type="email" class="donor-input" placeholder="Email for confirmation"/>
      </div>
    </div>
    <div class="modal-footer">
      <div class="modal-summary">Donating <strong id="summary-amount">$100</strong><span id="summary-freq">&middot; One Time</span></div>
      <button class="modal-submit" id="modal-submit" onclick="handleSubmit()">Donate <span id="submit-amount">$100</span></button>
    </div>
  </div>
</div>
<div class="modal-backdrop" id="confirm-backdrop" style="display:none">
  <div class="modal confirm-modal">
    <div class="confirm-icon">&#128332;</div>
    <h2 class="confirm-title">JazakAllahu Khayran!</h2>
    <p class="confirm-sub">May Allah accept your generous donation.</p>
    <div class="confirm-detail" id="confirm-detail"></div>
    <button class="modal-submit" onclick="closeConfirm()">Done</button>
  </div>
</div>
<button class="sticky-donate" onclick="openModal()" aria-label="Donate Now">
  <span class="sticky-heart">&#9829;</span><span class="sticky-text">DONATE NOW</span>
</button>`;

/* ── Inject shared components ─────────────────────────── */
function _injectShared() {
  const navPh    = document.getElementById('nav-ph');
  const footerPh = document.getElementById('footer-ph');
  const modalPh  = document.getElementById('modal-ph');
  if (navPh)    navPh.outerHTML    = NAV_HTML;
  if (footerPh) footerPh.outerHTML = FOOTER_HTML;
  if (modalPh)  modalPh.outerHTML  = MODAL_HTML;
}

/* ── Active nav link ──────────────────────────────────── */
function _setActive() {
  const seg = location.pathname.split('/').pop().replace('.html','') || 'index';
  document.querySelectorAll('.nav-menu a').forEach(a => {
    if (a.dataset.href === seg) a.classList.add('nav-active');
  });
}

/* ── Nav toggle ───────────────────────────────────────── */
function _initNav() {
  const tog  = document.getElementById('nav-toggle');
  const menu = document.getElementById('nav-menu');
  if (!tog) return;
  tog.addEventListener('click', () => {
    tog.classList.toggle('open');
    menu.classList.toggle('open');
  });
  window.addEventListener('scroll', () => {
    const nav = document.getElementById('site-nav');
    if (nav) nav.style.background = scrollY > 30 ? 'rgba(22,44,56,1)' : 'rgba(22,44,56,.97)';
  }, { passive:true });
}

/* ── Prayer strip ─────────────────────────────────────── */
function renderPrayerStrip() {
  const strip = document.getElementById('prayer-strip');
  if (!strip) return;
  const today = new Date();
  const times = _calcPT(today);
  const next  = _nextPrayer(times);
  const days  = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
  const months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
  const dateStr = `${days[today.getDay()]}, ${months[today.getMonth()]} ${today.getDate()}`;
  strip.innerHTML = `<div class="prayer-strip-inner">
    <div class="pstrip-date">${dateStr}</div>
    ${PRAYERS.map(p => `<div class="pstrip-item ${p.key === next ? 'next' : ''}">
      <span class="pstrip-name">${p.name}</span>
      <span class="pstrip-icon">${p.icon}</span>
      <span class="pstrip-time">${_fmt(times[p.key])}</span>
    </div>`).join('')}
  </div>`;
}

/* ── Prayer times page ────────────────────────────────── */
function renderPrayerPage() {
  const cards = document.getElementById('prayer-cards');
  const table = document.getElementById('prayer-table');
  if (!cards && !table) return;

  const today = new Date();
  const times = _calcPT(today);
  const next  = _nextPrayer(times);

  if (cards) {
    cards.innerHTML = PRAYERS.map(p => `
      <div class="prayer-card ${p.key === next ? 'current' : ''}">
        <div class="pc-icon">${p.icon}</div>
        <div class="pc-name">${p.name}</div>
        <div class="pc-time">${_fmt(times[p.key])}</div>
        <div class="pc-note">${p.note}</div>
      </div>`).join('');
  }

  if (table) {
    const days = ['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
    let rows = '';
    for (let i = 0; i < 7; i++) {
      const d = new Date(today); d.setDate(today.getDate() + i);
      const t = _calcPT(d);
      const isToday = i === 0;
      rows += `<tr class="${isToday ? 'today' : ''}">
        <td>${days[d.getDay()]} ${d.getMonth()+1}/${d.getDate()}</td>
        ${PRAYERS.map(p => `<td>${_fmt(t[p.key])}</td>`).join('')}
      </tr>`;
    }
    table.querySelector('tbody').innerHTML = rows;
  }
}

/* ── Home campaign bar ────────────────────────────────── */
function _initCampaignBar() {
  const bar = document.getElementById('home-camp-bar');
  if (!bar) return;
  const pct = Math.round(206000/350000*100);
  requestAnimationFrame(() => requestAnimationFrame(() => { bar.style.width = pct + '%'; }));
}

/* ── Scroll reveal ────────────────────────────────────── */
function _initReveal() {
  const els = document.querySelectorAll('.reveal');
  if (!els.length) return;
  const obs = new IntersectionObserver(entries => {
    entries.forEach(e => { if (e.isIntersecting) { e.target.classList.add('visible'); obs.unobserve(e.target); } });
  }, { threshold: 0.1 });
  els.forEach(el => obs.observe(el));
}

/* ── Event filter tabs ────────────────────────────────── */
function _initFilterTabs() {
  const tabs = document.querySelectorAll('.filter-tab');
  if (!tabs.length) return;
  tabs.forEach(tab => {
    tab.addEventListener('click', () => {
      tabs.forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      const cat = tab.dataset.cat;
      document.querySelectorAll('.event-card').forEach(card => {
        card.hidden = cat !== 'all' && card.dataset.cat !== cat;
      });
    });
  });
}

/* ── FAQ accordion ────────────────────────────────────── */
function _initFAQ() {
  document.querySelectorAll('.faq-q').forEach(q => {
    q.addEventListener('click', () => q.closest('.faq-item').classList.toggle('open'));
  });
}

/* ── Donate page amounts ──────────────────────────────── */
function _initDonateAmounts() {
  const btns = document.querySelectorAll('.d-amt-btn');
  if (!btns.length) return;
  btns.forEach(btn => {
    btn.addEventListener('click', () => {
      btns.forEach(b => b.classList.remove('selected'));
      btn.classList.add('selected');
      const v = btn.dataset.amount;
      const inp = document.getElementById('d-custom-inp');
      if (inp) inp.value = '';
      const submit = document.getElementById('d-submit-btn');
      if (submit) submit.textContent = `Donate $${Number(v).toLocaleString()} via Zelle`;
    });
  });
  const dfreq = document.querySelectorAll('.d-freq-btn');
  dfreq.forEach(b => b.addEventListener('click', () => { dfreq.forEach(x => x.classList.remove('active')); b.classList.add('active'); }));
}

/* ── Form handlers ────────────────────────────────────── */
async function _postForm(url, data, form, successEl) {
  const submit = form.querySelector('[type=submit]');
  if (submit) { submit.disabled = true; submit.textContent = 'Sending…'; }
  try {
    const res = await fetch(url, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify(data) });
    if (!res.ok) throw new Error('Server error');
    form.reset();
    if (successEl) { successEl.style.display = 'block'; }
    else if (typeof showToast === 'function') showToast('Message sent! We\'ll be in touch soon.');
  } catch {
    if (typeof showToast === 'function') showToast('Could not send. Please email us directly.');
  } finally {
    if (submit) { submit.disabled = false; submit.textContent = submit.dataset.label || 'Send'; }
  }
}

function _initContactForm() {
  const form = document.getElementById('contact-form');
  if (!form) return;
  form.addEventListener('submit', async e => {
    e.preventDefault();
    const fd = new FormData(form);
    await _postForm('/api/contact', { name: fd.get('name'), email: fd.get('email'), phone: fd.get('phone') || '', subject: fd.get('subject'), message: fd.get('message') }, form, document.getElementById('contact-success'));
  });
}

function _initVolunteerForm() {
  const form = document.getElementById('volunteer-form');
  if (!form) return;
  form.addEventListener('submit', async e => {
    e.preventDefault();
    const fd = new FormData(form);
    await _postForm('/api/volunteer', { name: fd.get('name'), email: fd.get('email'), phone: fd.get('phone') || '', role: fd.get('role'), availability: fd.get('availability') || '', message: fd.get('message') || '' }, form, document.getElementById('volunteer-success'));
  });
}

function _initNewsletter() {
  const form = document.getElementById('newsletter-form');
  if (!form) return;
  form.addEventListener('submit', async e => {
    e.preventDefault();
    const email = form.querySelector('input[type=email]')?.value?.trim();
    if (!email) return;
    try {
      await fetch('/api/newsletter', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email }) });
    } catch (_) { /* best-effort */ }
    if (typeof showToast === 'function') showToast('Subscribed! JazakAllahu Khayran.');
    form.reset();
  });
}

/* ── Boot ─────────────────────────────────────────────── */
document.addEventListener('DOMContentLoaded', () => {
  _injectShared();
  _setActive();
  _initNav();
  renderPrayerStrip();
  renderPrayerPage();
  _initCampaignBar();
  _initReveal();
  _initFilterTabs();
  _initFAQ();
  _initDonateAmounts();
  _initContactForm();
  _initVolunteerForm();
  _initNewsletter();
});
