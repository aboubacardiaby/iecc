/* ── Campaign data ───────────────────────────────────── */
const CAMPAIGN = { goal: 600000, raised: 206000 };

const NEEDS = [
  { icon: '🔥', label: 'Fire Sprinkler System Installation' },
  { icon: '🛡️', label: 'Safety & Occupancy Requirements' },
  { icon: '⚡', label: 'Electrical Upgrades' },
  { icon: '🔧', label: 'Plumbing Improvements' },
  { icon: '🏠', label: 'Interior Renovations' },
  { icon: '🕌', label: 'Prayer Hall Preparation' },
  { icon: '🚿', label: 'Wudu Facilities' }
];

const PRESET_AMOUNTS = [5, 25, 50, 100, 250, 500];
const MIN_DONATION = 5;

/* ── Modal state ─────────────────────────────────────── */
let selectedAmount = 5;
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
/* Both the PayPal tab and Card tab go through PayPal's Orders API:
   create-order → buyer approves → capture-order. The Card tab simply
   renders PayPal's hosted card button (fundingSource CARD), which lets
   donors pay by debit/credit card via guest checkout without this site
   ever handling raw card data. */
let ppSDKLoading  = false;
let ppSDKLoaded   = false;
let ppButtons     = null;
let ppCardButtons = null;

async function ensurePayPalSDK(loadingEl) {
  if (ppSDKLoaded) return true;
  if (ppSDKLoading) return false;
  ppSDKLoading = true;
  try {
    const res = await fetch('/api/paypal/client-id');
    if (!res.ok) throw new Error(`API error ${res.status}`);
    const { clientId } = await res.json();
    if (!clientId || clientId.startsWith('YOUR_')) {
      if (loadingEl) loadingEl.textContent = 'PayPal is not yet configured.';
      return false;
    }
    await new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = `https://www.paypal.com/sdk/js?client-id=${clientId}&currency=USD&intent=capture&enable-funding=card`;
      s.onload = resolve;
      s.onerror = () => reject(new Error(`PayPal SDK failed to load — client ID may be invalid or expired`));
      document.head.appendChild(s);
    });
    ppSDKLoaded = true;
    return true;
  } catch (err) {
    console.error('[PayPal]', err?.message || err);
    if (loadingEl) loadingEl.textContent = 'PayPal credentials are invalid or expired. Please contact the admin.';
    return false;
  } finally {
    ppSDKLoading = false;
  }
}

function makePPButtons(fundingSource, method) {
  return paypal.Buttons({
    fundingSource,
    style: { layout: 'vertical', color: fundingSource === paypal.FUNDING.CARD ? 'black' : 'gold', shape: 'rect', height: 45, ...(fundingSource !== paypal.FUNDING.CARD && { label: 'donate' }) },

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
        showConfirmation(method, captured.payerEmail);
        // Receipt is sent server-side automatically after capture
      } else {
        showToast('Payment could not be completed. Please try again.');
      }
    },

    onError:  ()  => showToast('Payment could not be processed. Please try another method.'),
    onCancel: ()  => showToast('Payment cancelled.')
  });
}

async function initPayPal() {
  if (ppButtons) return;
  const loading = document.getElementById('paypal-loading');
  if (!(await ensurePayPalSDK(loading))) return;
  if (loading) loading.style.display = 'none';
  ppButtons = makePPButtons(paypal.FUNDING.PAYPAL, 'paypal');
  ppButtons.render('#paypal-button-container');
}

async function initPayPalCard() {
  if (ppCardButtons) return;
  const loading = document.getElementById('card-loading');
  if (!(await ensurePayPalSDK(loading))) return;
  if (loading) loading.style.display = 'none';
  ppCardButtons = makePPButtons(paypal.FUNDING.CARD, 'card');
  ppCardButtons.render('#paypal-card-button-container');
}

/* ── Modal: payment tab ──────────────────────────────── */
function setPayTab(btn) {
  selectedTab = btn.dataset.tab;
  document.querySelectorAll('.pay-tab').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  document.querySelectorAll('.pay-panel').forEach(p => p.classList.add('hidden'));
  document.getElementById('panel-' + selectedTab).classList.remove('hidden');

  // Donor info: only for Zelle (PayPal and Card render their own checkout button)
  const donorSection = document.getElementById('donor-info-section');
  donorSection.style.display = selectedTab === 'zelle' ? '' : 'none';

  // Footer submit button: hidden for PayPal & Card (their PayPal buttons handle submission)
  const submitBtn    = document.getElementById('modal-submit');
  const summaryLine  = document.querySelector('.modal-summary');
  const usesPPButton = selectedTab === 'paypal' || selectedTab === 'card';
  submitBtn.style.display   = usesPPButton ? 'none' : '';
  summaryLine.style.display = usesPPButton ? 'none' : '';

  if (selectedTab === 'paypal') initPayPal();
  if (selectedTab === 'card')   initPayPalCard();

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
  if (selectedTab === 'zelle') {
    const name  = document.querySelectorAll('.donor-input')[0]?.value.trim() || '';
    const email = document.querySelectorAll('.donor-input')[1]?.value.trim() || '';
    showConfirmation('zelle', email);
    sendReceipt({
      donorName: name, donorEmail: email, amount: selectedAmount,
      method: 'Zelle', frequency: selectedFreq,
      transactionId: `ZELLE-${Date.now()}`
    });
  }
  // PayPal & Card: their rendered PayPal buttons handle their own submission
}

function showConfirmation(method, receiptEmail) {
  closeModal();

  const methodLabel  = { zelle: 'Zelle', paypal: 'PayPal', card: 'Credit/Debit Card' }[method];
  const freqLabel    = selectedFreq === 'monthly' ? 'Monthly donation' : 'One-time donation';
  const instructions = {
    zelle:  `Send <strong>${fmt(selectedAmount)}</strong> via Zelle to <strong>612-985-2768</strong><br>Enrolled as: IECC Masjid<br>Memo: <em>Masjid Donation</em>`,
    paypal: `Your PayPal payment of <strong>${fmt(selectedAmount)}</strong> has been received.<br>May Allah reward you for your generosity!`,
    card:   `Your card payment of <strong>${fmt(selectedAmount)}</strong> has been received.<br>May Allah reward you for your generosity!`
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
