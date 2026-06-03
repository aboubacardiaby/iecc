/* ── Campaign data ───────────────────────────────────── */
const CAMPAIGN = { goal: 350000, raised: 206000 };

const NEEDS = [
  { icon: '🔥', label: 'Fire Sprinkler System Installation' },
  { icon: '🛡️', label: 'Safety & Occupancy Requirements' },
  { icon: '⚡', label: 'Electrical Upgrades' },
  { icon: '🔧', label: 'Plumbing Improvements' },
  { icon: '🏠', label: 'Interior Renovations' },
  { icon: '🕌', label: 'Prayer Hall Preparation' },
  { icon: '🚿', label: 'Wudu Facilities' }
];

const PRESET_AMOUNTS = [100, 250, 500, 1000, 2500, 5000];
const MIN_DONATION = 100;

/* ── Modal state ─────────────────────────────────────── */
let selectedAmount = 100;
let selectedFreq   = 'one-time';
let selectedTab    = 'zelle';

function fmt(n) {
  return '$' + Number(n).toLocaleString('en-US');
}

/* ── Flyer: progress bar ─────────────────────────────── */
function renderProgress() {
  const pct         = Math.round((CAMPAIGN.raised / CAMPAIGN.goal) * 100);
  const pctEl       = document.getElementById('pct-val');
  const raisedEl    = document.getElementById('raised-val');
  const remainingEl = document.getElementById('remaining-val');
  const barEl       = document.getElementById('bar-fill');
  if (pctEl)       pctEl.textContent       = pct + '%';
  if (raisedEl)    raisedEl.textContent    = fmt(CAMPAIGN.raised);
  if (remainingEl) remainingEl.textContent = fmt(CAMPAIGN.goal - CAMPAIGN.raised);
  if (barEl) requestAnimationFrame(() => requestAnimationFrame(() => {
    barEl.style.width = pct + '%';
  }));
}

/* ── Flyer: needs list ───────────────────────────────── */
function renderNeeds() {
  const el = document.getElementById('needs-list');
  if (!el) return;
  el.innerHTML = NEEDS.map(n => `
    <li>
      <div class="need-check">✓</div>
      <span class="need-icon-sm">${n.icon}</span>
      <span>${n.label}</span>
    </li>
  `).join('');
}

function animateNeedItems() {
  const items = document.querySelectorAll('.needs-list li');
  if (!items.length) return;
  const obs = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;
      const i = [...items].indexOf(entry.target);
      setTimeout(() => entry.target.classList.add('visible'), i * 90);
      obs.unobserve(entry.target);
    });
  }, { threshold: 0.1 });
  items.forEach(li => obs.observe(li));
}

/* ── Modal: open / close ─────────────────────────────── */
function openModal() {
  renderAmountButtons();
  syncDisplay();
  document.getElementById('modal-backdrop').classList.add('open');
  document.body.style.overflow = 'hidden';
}

function closeModal() {
  document.getElementById('modal-backdrop').classList.remove('open');
  document.body.style.overflow = '';
}

function handleBackdropClick(e) {
  if (e.target === document.getElementById('modal-backdrop')) closeModal();
}

/* ── Modal: amount buttons ───────────────────────────── */
function renderAmountButtons() {
  const grid = document.getElementById('modal-amounts');
  grid.innerHTML = PRESET_AMOUNTS.map(a => `
    <button class="m-amt-btn ${a === selectedAmount ? 'selected' : ''}"
            onclick="selectAmount(${a}, this)">${fmt(a)}</button>
  `).join('');
}

function selectAmount(a, btn) {
  selectedAmount = a;
  document.querySelectorAll('.m-amt-btn').forEach(b => b.classList.remove('selected'));
  btn.classList.add('selected');
  document.getElementById('custom-amt').value = '';
  syncDisplay();
}

function handleCustomAmt(input) {
  const val = parseFloat(input.value);
  if (val >= MIN_DONATION) {
    selectedAmount = val;
    input.setCustomValidity('');
    document.querySelectorAll('.m-amt-btn').forEach(b => b.classList.remove('selected'));
    syncDisplay();
  } else if (val > 0) {
    input.setCustomValidity(`Minimum donation is $${MIN_DONATION}`);
    input.reportValidity();
  }
}

/* ── Modal: frequency ────────────────────────────────── */
function setFreq(btn) {
  selectedFreq = btn.dataset.freq;
  document.querySelectorAll('.freq-btn').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  syncDisplay();
}

/* ── PayPal SDK ───────────────────────────────────────── */
let ppSDKLoading = false;
let ppSDKLoaded  = false;
let ppButtons    = null;

async function initPayPal() {
  if (ppButtons) return;

  const container = document.getElementById('paypal-button-container');
  const loading   = document.getElementById('paypal-loading');

  if (!ppSDKLoaded) {
    if (ppSDKLoading) return;
    ppSDKLoading = true;
    try {
      const res = await fetch('/api/paypal/client-id');
      const { clientId } = await res.json();
      if (!clientId || clientId.startsWith('YOUR_')) {
        if (loading) loading.textContent = 'PayPal is not yet configured.';
        return;
      }
      await new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD&intent=capture`;
        s.onload = resolve;
        s.onerror = reject;
        document.head.appendChild(s);
      });
      ppSDKLoaded = true;
    } catch {
      if (loading) loading.textContent = 'Failed to load PayPal. Please try another method.';
      ppSDKLoading = false;
      return;
    }
  }

  if (loading) loading.style.display = 'none';

  ppButtons = paypal.Buttons({
    style: { layout: 'vertical', color: 'gold', shape: 'rect', label: 'donate', height: 45 },

    createOrder: async () => {
      const res = await fetch('/api/paypal/create-order', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({ amount: selectedAmount })
      });
      const data = await res.json();
      return data.id;
    },

    onApprove: async (data) => {
      const res = await fetch(`/api/paypal/capture-order/${data.orderID}`, { method: 'POST' });
      if (res.ok) {
        const captured = await res.json();
        showConfirmation('paypal', captured.payerEmail);
        sendReceipt({
          donorName:     captured.payerName  || '',
          donorEmail:    captured.payerEmail || '',
          amount:        selectedAmount,
          method:        'PayPal',
          frequency:     selectedFreq,
          transactionId: data.orderID
        });
      } else {
        showToast('Payment could not be completed. Please try again.');
      }
    },

    onError:  ()  => showToast('PayPal encountered an error. Please try another method.'),
    onCancel: ()  => showToast('Payment cancelled.')
  });

  ppButtons.render('#paypal-button-container');
}

/* ── Modal: payment tab ──────────────────────────────── */
function setPayTab(btn) {
  selectedTab = btn.dataset.tab;
  document.querySelectorAll('.pay-tab').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  document.querySelectorAll('.pay-panel').forEach(p => p.classList.add('hidden'));
  document.getElementById('panel-' + selectedTab).classList.remove('hidden');

  // Donor info: only for Zelle (PayPal and Card handle info themselves)
  const donorSection = document.getElementById('donor-info-section');
  donorSection.style.display = selectedTab === 'zelle' ? '' : 'none';

  // Footer submit button: hide for PayPal (its own button handles submission)
  const submitBtn   = document.getElementById('modal-submit');
  const summaryLine = document.querySelector('.modal-summary');
  const isPayPal    = selectedTab === 'paypal';
  submitBtn.style.display   = isPayPal ? 'none' : '';
  summaryLine.style.display = isPayPal ? 'none' : '';

  if (isPayPal) initPayPal();

  syncDisplay();
}

/* ── Sync all dynamic amount displays ───────────────── */
function syncDisplay() {
  const display  = fmt(selectedAmount);
  const freqText = selectedFreq === 'monthly' ? '· Monthly' : '· One Time';

  // Summary line
  document.getElementById('summary-amount').textContent = display;
  document.getElementById('summary-freq').textContent   = freqText;
  document.getElementById('submit-amount').textContent  = display;

  // Zelle / PayPal instruction amounts
  const zAmt = document.getElementById('zelle-amt-display');
  const pAmt = document.getElementById('paypal-amt-display');
  if (zAmt) zAmt.textContent = display;
  if (pAmt) pAmt.textContent = display;
}

/* ── Receipt sending (fire-and-forget) ───────────────── */
async function sendReceipt({ donorName, donorEmail, amount, method, frequency, transactionId }) {
  if (!donorEmail) return;
  try {
    await fetch('/api/receipt/send', {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ donorName, donorEmail, amount, method, frequency, transactionId })
    });
  } catch { /* receipt failure must never affect the donation flow */ }
}

/* ── Submit handler ──────────────────────────────────── */
function handleSubmit() {
  if (selectedAmount < MIN_DONATION) {
    showToast(`Minimum donation is $${MIN_DONATION}`);
    return;
  }
  if (selectedTab === 'paypal') {
    return; // PayPal SDK button handles its own submission
  } else if (selectedTab === 'zelle') {
    const name  = document.querySelectorAll('.donor-input')[0]?.value.trim() || '';
    const email = document.querySelectorAll('.donor-input')[1]?.value.trim() || '';
    showConfirmation('zelle', email);
    sendReceipt({
      donorName: name, donorEmail: email, amount: selectedAmount,
      method: 'Zelle', frequency: selectedFreq,
      transactionId: `ZELLE-${Date.now()}`
    });
  } else {
    // Card — basic validation
    const cardNum = document.getElementById('card-num').value.replace(/\s/g, '');
    if (cardNum.length < 13) { showToast('Please enter a valid card number'); return; }
    const cardEmail = document.querySelector('#panel-card input[type="email"]')?.value.trim() || '';
    showConfirmation('card', cardEmail);
    sendReceipt({
      donorName: '', donorEmail: cardEmail, amount: selectedAmount,
      method: 'Credit/Debit Card', frequency: selectedFreq,
      transactionId: `CARD-${Date.now()}`
    });
  }
}

function showConfirmation(method, receiptEmail) {
  closeModal();

  const methodLabel  = { zelle: 'Zelle', paypal: 'PayPal', card: 'Credit/Debit Card' }[method];
  const freqLabel    = selectedFreq === 'monthly' ? 'Monthly donation' : 'One-time donation';
  const instructions = {
    zelle:  `Send <strong>${fmt(selectedAmount)}</strong> via Zelle to <strong>612-985-2768</strong><br>Enrolled as: IECC Masjid<br>Memo: <em>Masjid Donation</em>`,
    paypal: `Your PayPal payment of <strong>${fmt(selectedAmount)}</strong> has been received.<br>May Allah reward you for your generosity!`,
    card:   `Your donation of <strong>${fmt(selectedAmount)}</strong> has been submitted.`
  }[method];

  const receiptLine = receiptEmail
    ? `<br><br><span class="receipt-sent">📧 Receipt sent to <strong>${receiptEmail}</strong></span>`
    : '';

  document.getElementById('confirm-detail').innerHTML =
    `${freqLabel} of <strong>${fmt(selectedAmount)}</strong> via ${methodLabel}<br><br>${instructions}${receiptLine}`;

  document.getElementById('confirm-backdrop').style.display = 'flex';
}

function closeConfirm() {
  document.getElementById('confirm-backdrop').style.display = 'none';
  document.body.style.overflow = '';
}

/* ── Card formatting helpers ─────────────────────────── */
function fmtCard(input) {
  let val = input.value.replace(/\D/g, '').slice(0, 16);
  input.value = val.replace(/(.{4})/g, '$1 ').trim();

  const brand = document.getElementById('card-brand');
  if (val.startsWith('4'))            brand.textContent = '💳 Visa';
  else if (/^5[1-5]/.test(val))      brand.textContent = '💳 MC';
  else if (/^3[47]/.test(val))       brand.textContent = '💳 Amex';
  else if (val.startsWith('6'))      brand.textContent = '💳 Disc';
  else                                brand.textContent = '';
}

function fmtExpiry(input) {
  let val = input.value.replace(/\D/g, '').slice(0, 4);
  if (val.length >= 3) val = val.slice(0, 2) + ' / ' + val.slice(2);
  input.value = val;
}

/* ── Copy to clipboard ───────────────────────────────── */
function copyText(btn) {
  navigator.clipboard.writeText(btn.dataset.copy).then(() => {
    const orig = btn.textContent;
    btn.textContent = 'Copied!';
    setTimeout(() => btn.textContent = orig, 1800);
    showToast('Copied: ' + btn.dataset.copy);
  }).catch(() => showToast(btn.dataset.copy));
}

function showToast(msg) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.classList.add('show');
  setTimeout(() => t.classList.remove('show'), 2400);
}

/* ── Keyboard: close modal on Escape ─────────────────── */
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    closeModal();
    closeConfirm();
  }
});

/* ── Landing page: campaign progress bar ─────────────── */
function renderCampaignBar() {
  const pct = Math.round((CAMPAIGN.raised / CAMPAIGN.goal) * 100);

  const campBar       = document.getElementById('camp-bar');
  const campPct       = document.getElementById('camp-pct');
  const campRaised    = document.getElementById('camp-raised');
  const campRemaining = document.getElementById('camp-remaining');
  const heroPct       = document.getElementById('hero-pct');
  const heroRaised    = document.getElementById('hero-raised');

  if (campPct)       campPct.textContent       = pct + '%';
  if (campRaised)    campRaised.textContent     = fmt(CAMPAIGN.raised);
  if (campRemaining) campRemaining.textContent  = fmt(CAMPAIGN.goal - CAMPAIGN.raised);
  if (heroPct)       heroPct.textContent        = pct + '%';
  if (heroRaised)    heroRaised.textContent     = fmt(CAMPAIGN.raised);

  if (campBar) {
    requestAnimationFrame(() => requestAnimationFrame(() => {
      campBar.style.width = pct + '%';
    }));
  }
}

/* ── Nav toggle (mobile) ─────────────────────────────── */
function closeNav() {
  document.getElementById('nav-links')?.classList.remove('open');
}

document.addEventListener('DOMContentLoaded', () => {
  const toggle = document.getElementById('nav-toggle');
  const links  = document.getElementById('nav-links');
  if (toggle && links) {
    toggle.addEventListener('click', () => links.classList.toggle('open'));
  }

  // Shrink nav on scroll
  const nav = document.getElementById('lp-nav');
  if (nav) {
    window.addEventListener('scroll', () => {
      nav.style.background = window.scrollY > 40
        ? 'rgba(22,44,56,0.99)'
        : 'rgba(22,44,56,0.96)';
    }, { passive: true });
  }
});

/* ── Boot ────────────────────────────────────────────── */
document.addEventListener('DOMContentLoaded', () => {
  renderProgress();
  renderNeeds();
  animateNeedItems();
  renderCampaignBar();
});
