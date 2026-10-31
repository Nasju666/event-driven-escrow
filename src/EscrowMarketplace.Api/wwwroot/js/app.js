/* Studio Escrow front end.
   Vanilla JS, no build step. Sidebar layout styled in css/app.css ("clean ledger").
   Every screen is role-specific: buyers book and pay, providers accept and finish sessions,
   admins settle disputes. Live updates arrive over SignalR. */
'use strict';

// ───────────────────────────── helpers ─────────────────────────────
const $ = (sel, root = document) => root.querySelector(sel);
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const ic = (name, size, cls) => (window.icon ? window.icon(name, size, cls) : '');
const USD = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' });
const money = (n) => USD.format(Number(n || 0));
const dayFmt = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: 'numeric', minute: '2-digit' });
const clockFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
const TZ = Intl.DateTimeFormat().resolvedOptions().timeZone;
const firstName = (n) => String(n || '').split(' ')[0];
const initials = (n) => String(n || '').split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join('') || '–';
const hoursLabel = (h) => (h === 1 ? '1 hour' : `${+h.toFixed(2)} hours`);
const whenLabel = (iso) => `${dayFmt.format(new Date(iso))}, ${timeFmt.format(new Date(iso))}`;
const pad2 = (n) => String(n).padStart(2, '0');

const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { if (v == null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* storage unavailable */ } }
};

function countdownText(iso) {
  const ms = Date.parse(iso) - Date.now();
  if (!(ms > 0)) return '0:00';
  const s = Math.floor(ms / 1000);
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}m`;
  return `${m}:${pad2(sec)}`;
}
const countdown = (iso) => `<time class="countdown" datetime="${esc(iso)}" data-deadline="${esc(iso)}">${countdownText(iso)}</time>`;

// ───────────────────────────── API client ─────────────────────────────
const Api = {
  token: store.get('escrow.token'),
  expiresAt: store.get('escrow.expiresAt'),

  get signedIn() {
    return !!this.token && (!this.expiresAt || Date.parse(this.expiresAt) > Date.now() + 10000);
  },

  setAuth(r) {
    this.token = r.token;
    this.expiresAt = r.expiresAt;
    store.set('escrow.token', r.token);
    store.set('escrow.expiresAt', r.expiresAt);
  },

  clear() {
    this.token = null;
    this.expiresAt = null;
    store.set('escrow.token', null);
    store.set('escrow.expiresAt', null);
  },

  async request(path, { method = 'GET', body } = {}) {
    const headers = {};
    if (this.token) headers.Authorization = `Bearer ${this.token}`;
    if (body !== undefined) headers['Content-Type'] = 'application/json';

    let res;
    try {
      res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    } catch {
      throw new Error("Can't reach the API. Check that the server is running.");
    }

    if (res.status === 401 && this.token) {
      App.signOut(true);
      throw new Error('Your session expired. Sign in again.');
    }

    const text = await res.text();
    let data = null;
    if (text) { try { data = JSON.parse(text); } catch { data = text; } }

    if (!res.ok) {
      const fieldErrors = data && data.errors ? Object.values(data.errors).flat().join(' ') : null;
      const err = new Error(fieldErrors || (data && (data.detail || data.title)) || `Request failed (${res.status}).`);
      err.status = res.status;
      throw err;
    }
    return data;
  },

  get(path) { return this.request(path); },
  post(path, body) { return this.request(path, { method: 'POST', body }); }
};

// ───────────────────────────── booking presentation ─────────────────────────────
const STATUS = {
  PendingPayment: { label: 'Awaiting payment', tone: 'neutral' },
  Held:           { label: 'Paid, awaiting provider', tone: 'held' },
  Confirmed:      { label: 'Confirmed', tone: 'held' },
  InService:      { label: 'In session', tone: 'held' },
  Completed:      { label: 'Session done', tone: 'held' },
  Disputed:       { label: 'In dispute', tone: 'frozen' },
  Settled:        { label: 'Paid out', tone: 'released' },
  Refunded:       { label: 'Refunded', tone: 'neutral' },
  Cancelled:      { label: 'Cancelled', tone: 'neutral' },
  Expired:        { label: 'Expired', tone: 'neutral' }
};
const BADGE = { held: 'accent', released: 'ok', frozen: 'bad', neutral: '' };
const FINISHED = ['Settled', 'Refunded', 'Cancelled', 'Expired'];

/** Bookings waiting on this user to do something. */
function needsYou(b, role) {
  if (role === 'Buyer') return b.status === 'PendingPayment' || b.status === 'Completed';
  if (role === 'Provider') return b.status === 'Held' || b.status === 'Confirmed' || b.status === 'InService';
  return false;
}

/** Where the money is right now, for the money trail. */
function moneyLocation(b) {
  switch (b.status) {
    case 'Held': case 'Confirmed': case 'InService': case 'Completed':
      return { at: 'escrow', tone: 'held', path: 1, back: false };
    case 'Disputed':
      return { at: 'escrow', tone: 'frozen', path: 1, back: false };
    case 'Settled':
      return { at: 'provider', tone: 'released', path: 2, back: false };
    case 'Refunded':
      return { at: 'buyer', tone: 'returned', path: 1, back: true };
    default: // PendingPayment, Cancelled, Expired: nothing moved
      return { at: 'buyer', tone: 'neutral', path: 0, back: false };
  }
}

function nextStep(b, role) {
  const total = money(b.totalAmount);
  const payout = money(b.providerPayoutAmount);
  const fee = money(b.platformCommissionAmount);
  const B = firstName(b.buyerName);
  const P = firstName(b.providerName);
  const reason = b.cancellationReason ? ` Reason given: “${esc(b.cancellationReason)}”` : '';

  if (role === 'Buyer') {
    switch (b.status) {
      case 'PendingPayment': return `Pay ${total} to keep this slot. It's released in ${countdown(b.paymentDueUtc)} if it isn't paid.`;
      case 'Held': return `${total} is held in escrow. ${esc(P)} has ${countdown(b.providerResponseDueUtc)} to accept, or you're refunded automatically.`;
      case 'Confirmed': case 'InService': return `${esc(P)} accepted. Your ${total} stays in escrow until the session is done.`;
      case 'Completed': return `${esc(P)} marked the session done. Release the payment if it went well, or open a dispute. It releases automatically in ${countdown(b.inspectionDeadlineUtc)}.`;
      case 'Disputed': return `Your dispute is open. The ${total} is frozen until an admin decides who gets it.`;
      case 'Settled': return `${payout} was paid to ${esc(P)}, plus a ${fee} platform fee.`;
      case 'Refunded': return `${total} was returned to your wallet.${reason}`;
      case 'Cancelled': return `Cancelled before payment, so nothing was charged.${reason}`;
      case 'Expired': return 'It wasn\'t paid in time, so the slot was released. Nothing was charged.';
    }
  }

  if (role === 'Provider') {
    switch (b.status) {
      case 'PendingPayment': return `Waiting for ${esc(B)} to pay. The slot is released in ${countdown(b.paymentDueUtc)} if they don't.`;
      case 'Held': return `${esc(B)} paid ${total} into escrow. Accept within ${countdown(b.providerResponseDueUtc)}, or the booking is cancelled and refunded automatically.`;
      case 'Confirmed': case 'InService': return `You accepted. Mark the session done when it's over to start the payout of ${payout}.`;
      case 'Completed': return `You marked it done. ${payout} is paid to you in ${countdown(b.inspectionDeadlineUtc)} unless ${esc(B)} opens a dispute.`;
      case 'Disputed': return `A dispute is open. The money is frozen until an admin decides.`;
      case 'Settled': return `${payout} was paid to your wallet. The platform kept ${fee}.`;
      case 'Refunded': return `${total} was refunded to ${esc(B)}.${reason}`;
      case 'Cancelled': return `Cancelled before payment.${reason}`;
      case 'Expired': return `${esc(B)} didn't pay in time, so the slot was released.`;
    }
  }

  switch (b.status) {
    case 'PendingPayment': return `Waiting for ${esc(B)} to pay. Released in ${countdown(b.paymentDueUtc)} if unpaid.`;
    case 'Held': return `Paid. ${esc(P)} has ${countdown(b.providerResponseDueUtc)} to accept.`;
    case 'Confirmed': case 'InService': return 'Accepted. Funds are held until the session is done.';
    case 'Completed': return `Done. The payout releases in ${countdown(b.inspectionDeadlineUtc)} unless ${esc(B)} disputes.`;
    case 'Disputed': return 'Dispute open. Settle it in Disputes.';
    case 'Settled': return `${payout} paid to ${esc(P)}, ${fee} platform fee.`;
    case 'Refunded': return `${total} refunded to ${esc(B)}.${reason}`;
    case 'Cancelled': return `Cancelled before payment.${reason}`;
    case 'Expired': return 'Not paid in time. Slot released.';
  }
  return '';
}

function bookingActions(b, role) {
  const btn = (action, label, cls, iconName) =>
    `<button class="btn btn-sm ${cls}" data-action="${action}" data-id="${b.id}">${iconName ? ic(iconName, 15) : ''}${esc(label)}</button>`;

  if (role === 'Buyer') {
    switch (b.status) {
      case 'PendingPayment': return [btn('pay', `Pay ${money(b.totalAmount)}`, 'btn-primary', 'wallet'), btn('cancel', 'Cancel', 'btn-ghost')];
      case 'Held': return [btn('cancel', 'Cancel and get a refund', 'btn-ghost')];
      case 'Confirmed': case 'InService': return [btn('dispute', 'Report a problem', 'btn-ghost btn-danger'), btn('cancel', 'Cancel and get a refund', 'btn-ghost')];
      case 'Completed': return [btn('approve', 'Release payment', 'btn-primary', 'circle-check'), btn('dispute', 'Open a dispute', 'btn-ghost btn-danger')];
    }
  }
  if (role === 'Provider') {
    switch (b.status) {
      case 'Held': return [btn('confirm', 'Accept booking', 'btn-primary', 'check'), btn('decline', 'Decline and refund', 'btn-ghost')];
      case 'Confirmed': case 'InService': return [btn('complete', 'Mark session done', 'btn-primary', 'circle-check'), btn('decline', 'Cancel and refund', 'btn-ghost')];
    }
  }
  return [];
}

// ───────────────────────────── app ─────────────────────────────
const App = {
  me: null,
  role: null,
  tab: null,
  bookings: [],
  resources: [],
  disputes: [],
  summary: null,
  demoUsers: [],
  filter: 'active',
  selectedRoom: null,
  form: null,
  openDetails: new Set(),
  lastAt: new Map(),
  flashId: null,
  hub: null,
  log: [],
  refreshTimer: null,
  refreshing: null,
  modalResolve: null,

  TABS: {
    Buyer: [
      { id: 'book', label: 'Book a room', icon: 'calendar' },
      { id: 'bookings', label: 'My bookings', icon: 'list-checks', count: true },
      { id: 'activity', label: 'Behind the scenes', short: 'Activity', icon: 'activity' }
    ],
    Provider: [
      { id: 'bookings', label: 'Bookings', icon: 'list-checks', count: true },
      { id: 'activity', label: 'Behind the scenes', short: 'Activity', icon: 'activity' }
    ],
    Admin: [
      { id: 'disputes', label: 'Disputes', icon: 'scale', count: true },
      { id: 'bookings', label: 'All bookings', icon: 'list-checks' },
      { id: 'ledger', label: 'Ledger', icon: 'landmark' },
      { id: 'activity', label: 'Behind the scenes', short: 'Activity', icon: 'activity' }
    ]
  },

  async init() {
    this.bindEvents();
    setInterval(() => this.tickCountdowns(), 1000);
    if (Api.signedIn) await this.start();
    else await this.showSignIn();
  },

  bindEvents() {
    $('#theme-toggle').addEventListener('click', () => this.toggleTheme());

    const account = $('#account');
    $('#account-btn').addEventListener('click', (e) => {
      e.stopPropagation();
      const open = account.classList.toggle('open');
      $('#account-btn').setAttribute('aria-expanded', String(open));
    });
    document.addEventListener('click', (e) => { if (!account.contains(e.target)) this.closeMenu(); });
    $('#sign-out').addEventListener('click', () => { this.closeMenu(); this.signOut(); });

    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      this.closeMenu();
      this.closeModal(null);
    });

    // One delegated handler for every data-action button in the app
    document.addEventListener('click', (e) => {
      const el = e.target.closest('[data-action]');
      if (!el) return;
      e.preventDefault();
      this.handle(el.dataset.action, el);
    });

    // Modal close buttons and backdrop
    const modal = $('#form-modal');
    modal.addEventListener('click', (e) => {
      if (e.target === modal || e.target.closest('[data-close]')) this.closeModal(null);
    });
  },

  // ── session ──
  async showSignIn() {
    this.setChrome(false);
    this.showPane('signin');
    const pane = $('#pane-signin');
    pane.innerHTML = `<div class="signin"><div class="empty"><span class="tile">${ic('loader-circle', 20, 'spin')}</span>Loading demo users…</div></div>`;

    let users = [];
    try {
      users = await Api.get('/api/auth/demo-users');
    } catch (e) {
      pane.innerHTML = `<div class="signin"><h1>Sign-in is unavailable</h1><p>${e.status === 404
        ? 'Demo sign-in is turned off on this server (Demo:PasswordlessLogin = false).'
        : esc(e.message)}</p></div>`;
      return;
    }

    const byTenant = users.reduce((acc, u) => ((acc[u.tenantName] ||= []).push(u), acc), {});
    pane.innerHTML = `
      <div class="signin">
        <h1>Book studio time. Pay through escrow.</h1>
        <p>Pick a demo user to see the marketplace from their side. Buyers pay into escrow, providers get paid once the session is done, and admins settle disputes.</p>
        ${Object.entries(byTenant).map(([tenant, list]) => `
          <div class="tenant-name">${esc(tenant)}</div>
          <div class="action-list">${list.map((u) => this.userRow(u)).join('')}</div>`).join('')}
        <div class="note">${ic('info', 15)}<span>There are no passwords in demo mode (turn it off with <code>Demo:PasswordlessLogin</code>). Northwind is a separate tenant: sign in as Lena to check she can't see anything from Apex.</span></div>
      </div>`;
  },

  userRow(u) {
    const meta = {
      Buyer: { tone: 'tone-held', icon: 'user-round', blurb: 'Books rooms and pays into escrow' },
      Provider: { tone: 'tone-released', icon: 'mic', blurb: 'Hosts rooms, accepts bookings, gets paid out' },
      Admin: { tone: 'tone-neutral', icon: 'scale', blurb: 'Settles disputes and watches the ledger' },
      Manager: { tone: 'tone-neutral', icon: 'scale', blurb: 'Settles disputes and watches the ledger' }
    }[u.role] || { tone: 'tone-neutral', icon: 'user-round', blurb: u.role };
    return `<button class="action-row" data-action="signin" data-email="${esc(u.email)}">
      <span class="tile ${meta.tone}">${ic(meta.icon, 18)}</span>
      <span class="text"><span class="title">${esc(u.fullName)}</span><span class="sub">${esc(u.role)}. ${esc(meta.blurb)}</span></span>
      ${ic('chevron-right', 16)}
    </button>`;
  },

  async signIn(email, el) {
    this.setBusy(el, true);
    try {
      const r = await Api.post('/api/auth/token', { email });
      this.resetState();
      Api.setAuth(r);
      await this.start();
    } catch (e) {
      this.toast(e.message, 'error');
    } finally {
      this.setBusy(el, false);
    }
  },

  async start() {
    try {
      this.me = await Api.get('/api/me');
    } catch (e) {
      if (!Api.signedIn) return this.showSignIn();
      this.toast(e.message, 'error');
      return;
    }

    this.role = this.me.role === 'Manager' ? 'Admin' : this.me.role;
    const [resources, demoUsers] = await Promise.all([
      this.role === 'Buyer' ? Api.get('/api/resources').catch(() => []) : Promise.resolve([]),
      Api.get('/api/auth/demo-users').catch(() => [])
    ]);
    this.resources = resources;
    this.demoUsers = demoUsers;
    this.selectedRoom = resources[0]?.id ?? null;

    this.setChrome(true);
    this.renderAccount();
    this.tab = this.TABS[this.role][0].id;
    this.addLog('sys', 'Signed in', `${this.me.fullName} (${this.me.role}) at ${this.me.tenantName}`);

    await this.refresh();
    this.showTab(this.tab);
    this.connectHub();
  },

  signOut(expired = false) {
    try { this.hub?.stop(); } catch { /* ignore */ }
    this.hub = null;
    Api.clear();
    this.resetState();
    this.showSignIn();
    if (expired) this.toast('Your session expired. Pick a user to sign in again.', 'warning');
  },

  resetState() {
    try { this.hub?.stop(); } catch { /* ignore */ }
    this.hub = null;
    Object.assign(this, { me: null, role: null, bookings: [], resources: [], disputes: [], summary: null, filter: 'active', form: null, log: [] });
    this.openDetails.clear();
    this.lastAt.clear();
    // Drop the previous user's rendered views so nothing of theirs stays in the page.
    document.querySelectorAll('.tab-pane:not(#pane-signin)').forEach((p) => { p.innerHTML = ''; });
    $('#switch-users').innerHTML = '';
  },

  setChrome(signedIn) {
    ['#live-chip', '#nav', '#account'].forEach((s) => $(s).classList.toggle('hidden', !signedIn));
    $('#side-hint').classList.toggle('hidden', signedIn);
    $('#wallet-chip').classList.toggle('hidden', !signedIn || this.role === 'Admin');
  },

  renderAccount() {
    const me = this.me;
    const avatar = $('#account-avatar');
    avatar.textContent = initials(me.fullName);
    avatar.className = `avatar${this.role === 'Provider' ? ' is-provider' : this.role === 'Admin' ? ' is-admin' : ''}`;
    $('#account-name').textContent = me.fullName;
    $('#account-role').textContent = `${me.role} at ${me.tenantName}`;
    $('#account-email').textContent = me.email;

    const others = this.demoUsers.filter((u) => u.email !== me.email);
    $('#switch-users').innerHTML = others.map((u) => `
      <button class="menu-item" role="menuitem" data-action="signin" data-email="${esc(u.email)}">
        ${ic(u.role === 'Provider' ? 'mic' : u.role === 'Buyer' ? 'user-round' : 'scale', 16)}
        ${esc(u.fullName)}
        <span class="hint">${esc(u.role)}${u.tenantId !== me.tenantId ? ', other tenant' : ''}</span>
      </button>`).join('');
  },

  renderWallet() {
    $('#wallet-balance').textContent = money(this.me?.walletBalance);
  },

  // ── data ──
  async refresh() {
    if (!this.me) return;
    if (this.refreshing) return this.refreshing;

    this.refreshing = (async () => {
      try {
        const [me, bookings, disputes, summary] = await Promise.all([
          Api.get('/api/me'),
          Api.get('/api/bookings?pageSize=100'),
          Api.get('/api/escrows/disputes').catch(() => []),
          this.role === 'Admin' ? Api.get('/api/analytics/reconciliation').catch(() => null) : Promise.resolve(null)
        ]);
        this.me = me;
        this.bookings = bookings;
        this.disputes = disputes;
        this.summary = summary;
        this.renderWallet();
        this.renderNav();
        this.renderPane(this.tab, { soft: true });
      } catch (e) {
        if (Api.signedIn) this.toast(e.message, 'error');
      } finally {
        this.refreshing = null;
      }
    })();
    return this.refreshing;
  },

  scheduleRefresh() {
    clearTimeout(this.refreshTimer);
    this.refreshTimer = setTimeout(() => this.refresh(), 350);
  },

  // ── navigation ──
  renderNav() {
    const counts = {
      bookings: this.bookings.filter((b) => needsYou(b, this.role)).length,
      disputes: this.disputes.filter((d) => d.status === 'PendingReview' || d.status === 'UnderArbitration').length
    };
    $('#nav').innerHTML = this.TABS[this.role].map((t) => `
      <button class="tab-btn${t.id === this.tab ? ' active' : ''}" data-action="tab" data-tab="${t.id}" aria-current="${t.id === this.tab ? 'page' : 'false'}">
        ${ic(t.icon, 16)}
        <span>${t.short ? `<span class="tab-label-long">${esc(t.label)}</span><span class="tab-label-short">${esc(t.short)}</span>` : esc(t.label)}</span>
        ${t.count && counts[t.id] ? `<span class="tab-count" title="Waiting on you">${counts[t.id]}</span>` : ''}
      </button>`).join('');
  },

  showTab(id) {
    this.tab = id;
    this.renderNav();
    this.showPane(id);
    this.renderPane(id, { soft: false });
  },

  showPane(id) {
    document.querySelectorAll('.tab-pane').forEach((p) => p.classList.toggle('active', p.id === `pane-${id}`));
  },

  renderPane(id, { soft }) {
    switch (id) {
      case 'book': if (!soft || !$('#book-form')) this.renderBook(); break;
      case 'bookings': this.renderBookings(); break;
      case 'disputes': this.renderDisputes(); break;
      case 'ledger': this.renderLedger(); break;
      case 'activity': if (!soft || !$('#console')) this.renderActivity(); break;
    }
  },

  // ── buyer: book a room ──
  renderBook() {
    const rooms = this.resources;
    const room = rooms.find((r) => r.id === this.selectedRoom) || rooms[0];
    if (!this.form) {
      const d = new Date(Date.now() + 86400000);
      this.form = { date: `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`, time: '14:00', hours: 2 };
    }

    $('#pane-book').innerHTML = `
      <div class="book">
        <div>
          <div class="page-head">
            <div>
              <h1>Book a room</h1>
              <p>Pick a room and a time. Your payment is held in escrow until the session is done.</p>
            </div>
          </div>
          <div class="rooms">
            ${rooms.length ? rooms.map((r) => `
              <button class="room" data-action="room" data-id="${r.id}" aria-pressed="${r.id === room?.id}">
                <span class="tile tone-held">${ic('mic', 20)}</span>
                <span class="room-text">
                  <span class="room-name">${esc(r.title)}</span>
                  <span class="room-desc">${esc(r.description)}</span>
                  <span class="room-host">Hosted by ${esc(r.providerName)}</span>
                </span>
                <span class="room-rate">${money(r.hourlyRate)}<small>/h</small></span>
              </button>`).join('') : `<div class="empty"><span class="tile">${ic('mic', 20)}</span><strong>No rooms listed yet</strong>Providers in your workspace haven't added any rooms.</div>`}
          </div>
        </div>

        ${room ? `
        <form class="card order" id="book-form" novalidate>
          <div class="order-head"><h2>${ic('calendar', 18)} New booking</h2></div>
          <div class="order-body">
            <div class="well">
              <div class="name">${esc(room.title)}</div>
              <div class="sub">${money(room.hourlyRate)} per hour, hosted by ${esc(room.providerName)}</div>
            </div>
            <div class="field-row">
              <div class="field"><label for="f-date">Date</label><input class="input" type="date" id="f-date" value="${this.form.date}" required></div>
              <div class="field"><label for="f-time">Start time</label><input class="input" type="time" id="f-time" step="900" value="${this.form.time}" required></div>
            </div>
            <div class="field">
              <label for="f-hours">Length</label>
              <select class="select" id="f-hours">
                ${[1, 2, 3, 4, 6, 8].map((h) => `<option value="${h}"${h === this.form.hours ? ' selected' : ''}>${hoursLabel(h)}</option>`).join('')}
              </select>
              <span class="help">Times are in your time zone (${esc(TZ)}).</span>
            </div>
          </div>
          <div class="order-foot">
            <div class="sum-row"><span id="f-calc"></span><span id="f-sub"></span></div>
            <div class="sum-row total"><span>Total</span><span id="f-total"></span></div>
            <button class="btn btn-primary btn-lg btn-block" type="submit" id="f-submit"></button>
            <div class="note">${ic('lock', 15)}<span>You pay in the next step. The money stays in escrow until the session is done, and unpaid reservations are released automatically.</span></div>
          </div>
        </form>` : ''}
      </div>`;

    const form = $('#book-form');
    if (!form) return;
    const update = () => {
      this.form = { date: $('#f-date').value, time: $('#f-time').value, hours: Number($('#f-hours').value) };
      const total = room.hourlyRate * this.form.hours;
      $('#f-calc').textContent = `${hoursLabel(this.form.hours)} at ${money(room.hourlyRate)}`;
      $('#f-sub').textContent = money(total);
      $('#f-total').textContent = money(total);
      $('#f-submit').textContent = `Reserve for ${money(total)}`;
    };
    form.addEventListener('input', update);
    form.addEventListener('submit', (e) => { e.preventDefault(); this.createBooking(room); });
    update();
  },

  async createBooking(room) {
    const { date, time, hours } = this.form;
    if (!date || !time) return this.toast('Pick a date and a start time.', 'warning');

    const start = new Date(`${date}T${time}`);
    if (Number.isNaN(start.getTime())) return this.toast('That date or time is not valid.', 'warning');
    if (start.getTime() < Date.now()) return this.toast('Pick a start time in the future.', 'warning');
    const end = new Date(start.getTime() + hours * 3600000);

    const btn = $('#f-submit');
    this.setBusy(btn, true);
    try {
      const b = await Api.post('/api/bookings', { resourceId: room.id, startTime: start.toISOString(), endTime: end.toISOString() });
      this.toast(`Reserved ${room.title} for ${whenLabel(b.startTime)}. Pay to keep the slot.`, 'success');
      this.flashId = b.id;
      this.filter = 'active';
      await this.refresh();
      this.showTab('bookings');
    } catch (e) {
      this.toast(e.status === 409 ? `That time overlaps another booking. ${e.message}` : e.message, 'error');
    } finally {
      this.setBusy(btn, false);
    }
  },

  // ── bookings list ──
  renderBookings() {
    const pane = $('#pane-bookings');
    if (this.role === 'Admin') return this.renderAllBookingsTable(pane);

    const active = this.bookings.filter((b) => !FINISHED.includes(b.status));
    const finished = this.bookings.filter((b) => FINISHED.includes(b.status));
    const list = (this.filter === 'finished' ? finished : active).slice();

    if (this.filter === 'finished') {
      list.sort((a, b) => Date.parse(b.startTime) - Date.parse(a.startTime));
    } else {
      list.sort((a, b) => (needsYou(b, this.role) - needsYou(a, this.role)) || (Date.parse(a.startTime) - Date.parse(b.startTime)));
    }

    const copy = this.role === 'Buyer'
      ? { h: 'My bookings', p: 'Money only leaves escrow when you release it, when the inspection window ends, or when an admin settles a dispute.' }
      : { h: 'Bookings', p: 'Paid bookings wait for you to accept them. Payouts reach your wallet once the buyer\'s inspection window ends.' };

    pane.innerHTML = `
      <div class="page-head">
        <div><h1>${copy.h}</h1><p>${copy.p}</p></div>
      </div>
      <div class="filters" role="tablist">
        <button class="category-pill${this.filter === 'active' ? ' active' : ''}" data-action="filter" data-filter="active" role="tab" aria-selected="${this.filter === 'active'}">Active <span class="count">${active.length}</span></button>
        <button class="category-pill${this.filter === 'finished' ? ' active' : ''}" data-action="filter" data-filter="finished" role="tab" aria-selected="${this.filter === 'finished'}">Finished <span class="count">${finished.length}</span></button>
      </div>
      <div class="bookings">
        ${list.length ? list.map((b) => this.bookingCard(b)).join('') : this.bookingsEmpty()}
      </div>`;

    this.openDetails.forEach((id) => { if ($(`#details-${id}`)) this.loadDetails(id); });

    if (this.flashId) {
      const card = $(`#booking-${this.flashId}`);
      if (card) {
        card.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'center' });
        card.classList.add('flash');
      }
      this.flashId = null;
    }
  },

  bookingsEmpty() {
    if (this.filter === 'finished') return `<div class="empty"><span class="tile">${ic('archive', 20)}</span><strong>Nothing finished yet</strong>Paid-out, refunded and cancelled bookings end up here.</div>`;
    if (this.role === 'Buyer') return `<div class="empty"><span class="tile">${ic('calendar', 20)}</span><strong>No active bookings</strong>Reserve a room to get started.<button class="btn btn-secondary btn-sm" data-action="tab" data-tab="book">Book a room</button></div>`;
    return `<div class="empty"><span class="tile">${ic('list-checks', 20)}</span><strong>No active bookings</strong>When a buyer reserves one of your rooms it shows up here.</div>`;
  },

  bookingCard(b) {
    const role = this.role;
    const st = STATUS[b.status] || { label: b.status, tone: 'neutral' };
    const start = new Date(b.startTime);
    const end = new Date(b.endTime);
    const hours = (end - start) / 3600000;
    const counterpart = role === 'Provider' ? `booked by ${b.buyerName}` : `hosted by ${b.providerName}`;
    const actions = bookingActions(b, role);
    const open = this.openDetails.has(b.id);

    return `
      <article class="card booking" id="booking-${b.id}">
        <div class="booking-head">
          <span class="tile tone-${st.tone === 'neutral' ? 'neutral' : st.tone}">${ic('mic', 18)}</span>
          <div class="booking-title">
            <h3>${esc(b.resourceTitle)}</h3>
            <p class="booking-when">${dayFmt.format(start)}, ${timeFmt.format(start)} to ${timeFmt.format(end)} <span class="with">(${hoursLabel(hours)}, ${esc(counterpart)})</span></p>
          </div>
          <div class="booking-amount">
            <div class="total">${money(b.totalAmount)}</div>
            <span class="badge dot ${BADGE[st.tone]}">${st.label}</span>
          </div>
        </div>
        <p class="booking-next">${nextStep(b, role)}</p>
        ${this.trail(b)}
        <div class="booking-foot">
          <div class="booking-actions">${actions.join('')}</div>
          <button class="btn btn-ghost btn-sm" data-action="details" data-id="${b.id}" aria-expanded="${open}" aria-controls="details-${b.id}">
            ${ic('file-text', 15)} ${open ? 'Hide escrow ledger' : 'Escrow ledger'}
          </button>
        </div>
        <div class="booking-details${open ? '' : ' hidden'}" id="details-${b.id}"></div>
      </article>`;
  },

  trail(b) {
    const where = moneyLocation(b);
    const key = `${where.at}:${where.tone}`;
    const prev = this.lastAt.get(b.id);
    this.lastAt.set(b.id, key);
    const arrived = prev !== undefined && prev !== key;

    const buyerWho = this.role === 'Buyer' ? 'You (buyer)' : `${firstName(b.buyerName)} (buyer)`;
    const providerWho = this.role === 'Provider' ? 'You (provider)' : `${firstName(b.providerName)} (provider)`;
    const node = (id, iconName, who, amt) => `
      <div class="trail-node${where.at === id ? ' is-here' : ''}${arrived && where.at === id ? ' just-arrived' : ''}">
        <span class="tile">${ic(iconName, 15)}</span>
        <span class="node-text"><span class="who">${esc(who)}</span><span class="amt">${amt}</span></span>
      </div>`;
    const link = (n) => `<div class="trail-link${where.path >= n && !(where.back && n === 2) ? ' is-path' : ''}${where.back && n === 1 ? ' back' : ''}"></div>`;

    const label = {
      'buyer:neutral': 'The money is still with the buyer.',
      'escrow:held': 'The money is held in escrow.',
      'escrow:frozen': 'The money is frozen in escrow while the dispute is open.',
      'provider:released': 'The money was released to the provider.',
      'buyer:returned': 'The money went back to the buyer.'
    }[key];

    return `
      <div class="trail" data-tone="${where.tone}" role="img" aria-label="${esc(label)}">
        ${node('buyer', 'wallet', buyerWho, money(b.totalAmount))}
        ${link(1)}
        ${node('escrow', 'lock', 'Escrow', money(b.totalAmount))}
        ${link(2)}
        ${node('provider', 'banknote', providerWho, money(b.providerPayoutAmount) + (b.platformCommissionAmount ? ` <span class="muted fee">+ ${money(b.platformCommissionAmount)} fee</span>` : ''))}
      </div>`;
  },

  async loadDetails(id) {
    const box = $(`#details-${id}`);
    if (!box) return;
    if (!box.innerHTML) box.innerHTML = `<div class="muted">Loading…</div>`;
    try {
      const [escrow, workflow] = await Promise.all([
        Api.get(`/api/escrows/booking/${id}`),
        Api.get(`/api/bookings/${id}/workflow`).catch(() => null)
      ]);
      const names = { HoldDeposit: 'Buyer deposit into escrow', ProviderPayout: 'Payout to provider', PlatformFee: 'Platform fee', RefundToBuyer: 'Refund to buyer', DisputeAdjustment: 'Dispute adjustment' };
      const wf = workflow
        ? `<div class="workflow-line"><span>Workflow state <code>${esc(workflow.state)}</code></span>${workflow.nextDeadlineUtc ? `<span>Next deadline: <code>${esc(workflow.nextDeadlineKind)}</code> in ${countdown(workflow.nextDeadlineUtc)}</span>` : ''}${escrow.paymentReference ? `<span>Payment reference <code>${esc(escrow.paymentReference)}</code></span>` : ''}</div>`
        : `<div class="workflow-line"><span>Workflow state is not available in in-memory mode.</span></div>`;
      const rows = escrow.ledgerEntries.map((l) => `
        <tr><td>${esc(names[l.entryType] || l.entryType)}</td><td class="r num">${money(l.amount)}</td><td class="muted">${whenLabel(l.createdAt)}</td></tr>`).join('');
      box.innerHTML = `${wf}
        <div class="table-wrap" style="margin:0;padding:0">
          <table class="data-table">
            <thead><tr><th>Entry</th><th class="r">Amount</th><th>When</th></tr></thead>
            <tbody>${rows || '<tr class="empty-row"><td colspan="3">No money has moved yet.</td></tr>'}</tbody>
          </table>
        </div>`;
    } catch (e) {
      box.innerHTML = `<div class="muted">${esc(e.message)}</div>`;
    }
  },

  renderAllBookingsTable(pane) {
    const rows = this.bookings.slice().sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
    pane.innerHTML = `
      <div class="page-head"><div><h1>All bookings</h1><p>Every booking in ${esc(this.me.tenantName)}, newest first.</p></div></div>
      <div class="card card-pad">
        <div class="table-wrap">
          <table class="data-table">
            <thead><tr><th>Room</th><th>When</th><th>Buyer</th><th>Provider</th><th class="r">Amount</th><th>Status</th></tr></thead>
            <tbody>
              ${rows.length ? rows.map((b) => {
                const st = STATUS[b.status] || { label: b.status, tone: 'neutral' };
                return `<tr>
                  <td class="strong">${esc(b.resourceTitle)}</td>
                  <td>${whenLabel(b.startTime)}</td>
                  <td>${esc(b.buyerName)}</td>
                  <td>${esc(b.providerName)}</td>
                  <td class="r num">${money(b.totalAmount)}</td>
                  <td><span class="badge dot ${BADGE[st.tone]}">${st.label}</span></td>
                </tr>`;
              }).join('') : '<tr class="empty-row"><td colspan="6">No bookings yet.</td></tr>'}
            </tbody>
          </table>
        </div>
      </div>`;
  },

  // ── admin: disputes & ledger ──
  renderDisputes() {
    const open = this.disputes.filter((d) => d.status === 'PendingReview' || d.status === 'UnderArbitration');
    const closed = this.disputes.filter((d) => !open.includes(d));
    const booking = (id) => this.bookings.find((b) => b.id === id);

    $('#pane-disputes').innerHTML = `
      <div class="page-head">
        <div><h1>Disputes</h1><p>When a buyer or provider reports a problem, the money stays frozen in escrow until you decide who gets it.</p></div>
      </div>
      <div class="bookings">
        ${open.length ? open.map((d) => {
          const b = booking(d.bookingId);
          return `
          <article class="card booking">
            <div class="booking-head">
              <span class="tile tone-frozen">${ic('scale', 18)}</span>
              <div class="booking-title">
                <h3>${esc(b ? b.resourceTitle : 'Booking')}</h3>
                <p class="booking-when">${b ? `${whenLabel(b.startTime)} <span class="with">(${esc(b.buyerName)} with ${esc(b.providerName)})</span>` : ''}</p>
              </div>
              <div class="booking-amount"><div class="total">${b ? money(b.totalAmount) : ''}</div><span class="badge dot bad">Frozen</span></div>
            </div>
            <p class="dispute-reason">“${esc(d.reason)}”</p>
            <dl class="dispute-meta">
              <dt>Raised by</dt><dd>${esc(d.raisedByUserName)}, ${whenLabel(d.createdAt)}</dd>
              <dt>Evidence</dt><dd>${esc(d.evidenceSummary || 'None provided')}</dd>
            </dl>
            <div class="booking-actions">
              <button class="btn btn-secondary btn-sm" data-action="resolve-buyer" data-id="${d.id}">${ic('rotate-ccw', 15)} Refund ${b ? esc(firstName(b.buyerName)) : 'the buyer'} ${b ? money(b.totalAmount) : ''}</button>
              <button class="btn btn-secondary btn-sm" data-action="resolve-provider" data-id="${d.id}">${ic('banknote', 15)} Pay ${b ? esc(firstName(b.providerName)) : 'the provider'} ${b ? money(b.providerPayoutAmount) : ''}</button>
            </div>
          </article>`;
        }).join('') : `<div class="empty"><span class="tile">${ic('scale', 20)}</span><strong>No open disputes</strong>Nothing is frozen right now.</div>`}
      </div>
      ${closed.length ? `
        <div class="card card-pad" style="margin-top:1.25rem">
          <div class="card-head"><div><div class="card-title">Resolved</div></div></div>
          <div class="table-wrap">
            <table class="data-table">
              <thead><tr><th>Booking</th><th>Reason</th><th>Outcome</th><th>Notes</th></tr></thead>
              <tbody>${closed.map((d) => `<tr>
                <td class="strong">${esc(booking(d.bookingId)?.resourceTitle || '—')}</td>
                <td>${esc(d.reason)}</td>
                <td><span class="badge">${d.status === 'ResolvedInFavorOfBuyer' ? 'Buyer refunded' : 'Provider paid'}</span></td>
                <td class="muted">${esc(d.resolutionNotes || '')}</td>
              </tr>`).join('')}</tbody>
            </table>
          </div>
        </div>` : ''}`;
  },

  renderLedger() {
    const s = this.summary;
    const kpi = (label, iconName, tone, value, sub, glow) => `
      <div class="card kpi${glow ? ' glow' : ''}">
        <div class="kpi-head"><span class="kpi-label"><span class="tile ${tone}">${ic(iconName, 15)}</span>${label}</span></div>
        <div class="kpi-value">${value}</div>
        <div class="kpi-sub">${sub}</div>
      </div>`;

    $('#pane-ledger').innerHTML = `
      <div class="page-head"><div><h1>Ledger</h1><p>Money across ${esc(this.me.tenantName)} for the last 30 days, straight from the escrow ledger.</p></div></div>
      ${s ? `
      <div class="kpis three">
        ${kpi('Held in escrow', 'lock', 'tone-held', money(s.totalHeldInEscrow), `${s.activeEscrowHoldsCount} ${s.activeEscrowHoldsCount === 1 ? 'booking' : 'bookings'}, ${s.activeDisputesCount} frozen by disputes`, true)}
        ${kpi('Paid to providers', 'banknote', 'tone-released', money(s.totalDisbursedToProviders), `${s.settledBookingsCount} settled ${s.settledBookingsCount === 1 ? 'booking' : 'bookings'}`)}
        ${kpi('Platform fees', 'landmark', 'tone-released', money(s.totalPlatformFeesCaptured), 'Kept on settled bookings')}
        ${kpi('Refunded to buyers', 'rotate-ccw', 'tone-neutral', money(s.totalRefundedToBuyers), 'Cancellations and disputes')}
      </div>
      <div class="note">${ic('info', 15)}<span>Every payment into escrow equals its payout plus fee, or its refund. A nightly Hangfire job runs this same reconciliation for every tenant.</span></div>`
      : `<div class="empty"><span class="tile">${ic('landmark', 20)}</span><strong>Ledger unavailable</strong>The analytics endpoint needs PostgreSQL.</div>`}`;
  },

  // ── behind the scenes ──
  renderActivity() {
    const rooms = this.resources;
    $('#pane-activity').innerHTML = `
      <div class="page-head">
        <div><h1>Behind the scenes</h1><p>What the backend does as you click: domain events delivered through the transactional outbox, and escrow workflow (saga) transitions, pushed live over SignalR.</p></div>
      </div>
      <div class="scenes">
        <div class="card card-pad">
          <div class="card-head">
            <div><div class="card-title">Live activity</div><div class="card-sub">${this.role === 'Admin' ? 'Every booking in your workspace.' : 'Only bookings you are part of.'} Open a second browser as another user to watch both sides.</div></div>
            <button class="btn btn-ghost btn-sm" data-action="clear-log">Clear</button>
          </div>
          <div class="console" id="console"></div>
        </div>
        <div class="stack">
          ${this.role === 'Buyer' && rooms.length ? `
          <div class="card card-pad">
            <div class="card-head"><div>
              <div class="card-title">Double-booking test</div>
              <div class="card-sub">Sends 8 identical requests for the same slot at the same moment. They all pass the app's availability check together, so only the database can stop them. PostgreSQL's exclusion constraint lets exactly one in.</div>
            </div></div>
            <div class="field"><label for="race-room">Room</label>
              <select class="select" id="race-room">${rooms.map((r) => `<option value="${r.id}">${esc(r.title)}</option>`).join('')}</select>
            </div>
            <button class="btn btn-secondary" data-action="race">${ic('zap', 15)} Send 8 requests at once</button>
            <div id="race-result"></div>
          </div>` : ''}
          <div class="card card-pad">
            <div class="card-head"><div><div class="card-title">How a booking moves</div><div class="card-sub">Each deadline is a scheduled message held by the escrow saga, not a polling job.</div></div></div>
            <ol class="timeline">
              <li><span class="step">1</span><span><strong>Reserve.</strong> The slot is held and a payment timer starts. Unpaid bookings expire.</span></li>
              <li><span class="step">2</span><span><strong>Pay.</strong> Money moves from the buyer's wallet into escrow. The provider gets a response timer, or the buyer is refunded.</span></li>
              <li><span class="step">3</span><span><strong>Accept, then mark done.</strong> Marking the session done starts the inspection window.</span></li>
              <li><span class="step">4</span><span><strong>Settle.</strong> The buyer releases early, opens a dispute, or the window ends and the payout releases automatically.</span></li>
            </ol>
            <div class="links">
              <a class="action-row" href="/scalar/v1" target="_blank" rel="noopener"><span class="tile tone-held">${ic('book-open-text', 16)}</span><span class="text"><span class="title">API reference</span><span class="sub">Scalar, OpenAPI</span></span>${ic('external-link', 15)}</a>
              <a class="action-row" href="/hangfire" target="_blank" rel="noopener"><span class="tile tone-neutral">${ic('server-cog', 16)}</span><span class="text"><span class="title">Background jobs</span><span class="sub">Hangfire dashboard (local only)</span></span>${ic('external-link', 15)}</a>
              <a class="action-row" href="http://localhost:15672" target="_blank" rel="noopener"><span class="tile tone-neutral">${ic('waves', 16)}</span><span class="text"><span class="title">Message broker</span><span class="sub">RabbitMQ management, guest / guest</span></span>${ic('external-link', 15)}</a>
            </div>
          </div>
        </div>
      </div>`;
    this.renderConsole();
  },

  addLog(kind, name, message, bookingId) {
    const room = bookingId ? this.bookings.find((b) => b.id === bookingId)?.resourceTitle : null;
    this.log.push({ at: new Date(), kind, name, message, room });
    if (this.log.length > 200) this.log.shift();
    this.renderConsole();
  },

  renderConsole() {
    const el = $('#console');
    if (!el) return;
    const cls = { wf: 'wf', ev: 'ev', er: 'er', sys: 'k' };
    el.innerHTML = this.log.map((l) => `
      <div class="line"><span class="t">${clockFmt.format(l.at)}</span><span><span class="${cls[l.kind] || 'k'}">${l.kind === 'wf' ? `saga → ${esc(l.name)}` : esc(l.name)}</span>${l.room ? ` <span class="k">${esc(l.room)}</span>` : ''} ${esc(l.message || '')}</span></div>`).join('');
    el.scrollTop = el.scrollHeight;
  },

  async runRace(btn) {
    const resourceId = $('#race-room').value;
    const day = 7 + Math.floor(Math.random() * 60);
    const start = new Date();
    start.setDate(start.getDate() + day);
    start.setHours(9 + Math.floor(Math.random() * 9), 0, 0, 0);
    const end = new Date(start.getTime() + 3600000);
    const body = { resourceId, startTime: start.toISOString(), endTime: end.toISOString() };

    this.setBusy(btn, true);
    const results = await Promise.all(Array.from({ length: 8 }, () =>
      Api.post('/api/bookings', body).then(() => 'ok', (e) => (e.status === 409 ? 'conflict' : 'other'))));
    this.setBusy(btn, false);

    const ok = results.filter((r) => r === 'ok').length;
    const conflict = results.filter((r) => r === 'conflict').length;
    const other = results.length - ok - conflict;
    this.addLog(ok === 1 ? 'ev' : 'er', 'Double-booking test', `${ok} accepted, ${conflict} rejected with 409${other ? `, ${other} other errors` : ''} for ${whenLabel(body.startTime)}`);

    $('#race-result').innerHTML = `
      <div class="race-result">
        <div class="race-stat ok"><div class="label">${ic('circle-check', 14)} Accepted</div><div class="value">${ok}</div></div>
        <div class="race-stat bad"><div class="label">${ic('circle-alert', 14)} Rejected (409)</div><div class="value">${conflict}</div></div>
      </div>
      <div class="note">${ic('info', 15)}<span>${ok === 1
        ? `Exactly one request won for ${whenLabel(body.startTime)}. It's in My bookings; cancel it or let it expire.`
        : `Expected exactly one winner but got ${ok}. Check that the exclusion constraint exists (it is created at startup).`}</span></div>`;
    this.scheduleRefresh();
  },

  // ── actions ──
  async handle(action, el) {
    const id = el.dataset.id;
    const b = id ? this.bookings.find((x) => x.id === id) : null;

    switch (action) {
      case 'signin': this.closeMenu(); return this.signIn(el.dataset.email, el);
      case 'tab': return this.showTab(el.dataset.tab);
      case 'filter': this.filter = el.dataset.filter; return this.renderBookings();
      case 'room': this.selectedRoom = id; return this.renderBook();
      case 'clear-log': this.log = []; return this.renderConsole();
      case 'race': return this.runRace(el);
      case 'details': {
        const open = !this.openDetails.has(id);
        if (open) this.openDetails.add(id); else this.openDetails.delete(id);
        const box = $(`#details-${id}`);
        box.classList.toggle('hidden', !open);
        el.setAttribute('aria-expanded', String(open));
        el.innerHTML = `${ic('file-text', 15)} ${open ? 'Hide escrow ledger' : 'Escrow ledger'}`;
        if (open) { box.innerHTML = ''; this.loadDetails(id); }
        return;
      }

      case 'pay':
        return this.run(el, () => Api.post(`/api/bookings/${id}/pay`),
          (r) => `Paid ${money(r.totalAmount)} into escrow. ${firstName(r.providerName)} has been asked to accept.`);
      case 'confirm':
        return this.run(el, () => Api.post(`/api/bookings/${id}/confirm`),
          () => 'Booking accepted. Mark it done after the session to start your payout.');
      case 'complete':
        return this.run(el, () => Api.post(`/api/bookings/${id}/complete`),
          (r) => `Marked done. ${firstName(r.buyerName)} can dispute until ${whenLabel(r.inspectionDeadlineUtc)}, then the payout releases.`);
      case 'approve':
        return this.run(el, () => Api.post(`/api/bookings/${id}/approve`),
          (r) => `Payment released. ${money(r.providerPayoutAmount)} went to ${firstName(r.providerName)}.`);

      case 'cancel':
      case 'decline': {
        const isDecline = action === 'decline';
        const values = await this.openForm({
          title: isDecline ? (b.status === 'Held' ? 'Decline this booking?' : 'Cancel this booking?') : 'Cancel this booking?',
          desc: b.escrowStatus === 'Funded'
            ? `${money(b.totalAmount)} goes back to ${this.role === 'Buyer' ? 'your' : `${firstName(b.buyerName)}'s`} wallet straight away.`
            : 'Nothing has been paid yet, so nothing is charged.',
          tone: 'tone-frozen', icon: 'circle-alert',
          fields: [{ label: 'Reason', type: 'textarea', value: isDecline ? 'The room is not available at that time.' : 'My plans changed.', required: true }],
          submit: isDecline ? (b.status === 'Held' ? 'Decline and refund' : 'Cancel and refund') : (b.escrowStatus === 'Funded' ? 'Cancel and get a refund' : 'Cancel booking'),
          danger: true
        });
        if (!values) return;
        return this.run(el, () => Api.post(`/api/bookings/${id}/cancel`, { reason: values[0] }),
          (r) => (r.status === 'Refunded' ? `Cancelled. ${money(r.totalAmount)} was refunded to ${firstName(r.buyerName)}.` : 'Booking cancelled.'));
      }

      case 'dispute': {
        const values = await this.openForm({
          title: 'Open a dispute',
          desc: `The ${money(b.totalAmount)} stays frozen in escrow until an admin decides. Automatic release stops.`,
          tone: 'tone-frozen', icon: 'scale',
          fields: [
            { label: 'What went wrong?', type: 'textarea', value: '', placeholder: 'The booth microphone was broken for most of the session.', required: true },
            { label: 'Evidence (optional)', type: 'text', value: '', placeholder: 'Photos, recordings, messages' }
          ],
          submit: 'Open dispute',
          danger: true
        });
        if (!values) return;
        return this.run(el, () => Api.post(`/api/bookings/${id}/dispute`, { reason: values[0], evidenceSummary: values[1] }),
          () => 'Dispute opened. The money is frozen until an admin decides.');
      }

      case 'resolve-buyer':
      case 'resolve-provider': {
        const forBuyer = action === 'resolve-buyer';
        const d = this.disputes.find((x) => x.id === id);
        const bk = d && this.bookings.find((x) => x.id === d.bookingId);
        const values = await this.openForm({
          title: forBuyer ? 'Refund the buyer?' : 'Pay the provider?',
          desc: bk ? (forBuyer
            ? `${money(bk.totalAmount)} goes back to ${bk.buyerName}. The provider gets nothing.`
            : `${money(bk.providerPayoutAmount)} goes to ${bk.providerName}, and the platform keeps ${money(bk.platformCommissionAmount)}.`) : '',
          tone: forBuyer ? 'tone-neutral' : 'tone-released', icon: forBuyer ? 'rotate-ccw' : 'banknote',
          fields: [{ label: 'Decision notes (both sides can see these)', type: 'textarea', value: '', required: true }],
          submit: forBuyer ? 'Refund buyer' : 'Pay provider'
        });
        if (!values) return;
        return this.run(el, () => Api.post(`/api/escrows/disputes/${id}/resolve`, {
          resolution: forBuyer ? 'ResolvedInFavorOfBuyer' : 'ResolvedInFavorOfProvider',
          resolutionNotes: values[0]
        }), () => (forBuyer ? 'Dispute settled. The buyer was refunded.' : 'Dispute settled. The provider was paid.'));
      }
    }
  },

  async run(el, call, success) {
    this.setBusy(el, true);
    try {
      const result = await call();
      this.toast(success(result), 'success');
      await this.refresh();
    } catch (e) {
      this.toast(e.message, 'error');
      this.addLog('er', 'Request failed', e.message);
    } finally {
      this.setBusy(el, false);
    }
  },

  setBusy(el, busy) {
    if (!el || !el.isConnected) return;
    el.disabled = busy;
    el.classList.toggle('is-busy', busy);
    if (busy) {
      el.dataset.html = el.innerHTML;
      if (el.classList.contains('btn')) el.innerHTML = `${ic('loader-circle', 15, 'spin')} Working…`;
    } else if (el.dataset.html) {
      el.innerHTML = el.dataset.html;
      delete el.dataset.html;
    }
  },

  // ── live updates ──
  async connectHub() {
    if (!window.signalR || !Api.token) return this.setLive(false);
    try { await this.hub?.stop(); } catch { /* ignore */ }

    const hub = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/marketplace', { accessTokenFactory: () => Api.token })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    hub.on('BookingUpdated', (u) => {
      this.addLog(u.kind === 'workflow' ? 'wf' : 'ev', u.name, u.message, u.bookingId);
      this.scheduleRefresh();
    });
    hub.onreconnecting(() => this.setLive(false));
    hub.onreconnected(() => { this.setLive(true); this.scheduleRefresh(); });
    hub.onclose(() => this.setLive(false));
    this.hub = hub;

    try {
      await hub.start();
      this.setLive(true);
      this.addLog('sys', 'Live updates', 'Connected to /hubs/marketplace');
    } catch (e) {
      this.setLive(false);
      this.addLog('er', 'Live updates unavailable', e.message);
    }
  },

  setLive(on) {
    $('#live-dot').classList.toggle('off', !on);
    $('#live-text').textContent = on ? 'Live' : 'Offline';
    $('#live-chip').title = on ? 'Live updates are on' : 'Live updates are off. The page still works; changes show after your next action.';
  },

  tickCountdowns() {
    document.querySelectorAll('.countdown[data-deadline]').forEach((el) => {
      el.textContent = countdownText(el.dataset.deadline);
      el.classList.toggle('soon', Date.parse(el.dataset.deadline) - Date.now() < 60000);
    });
  },

  // ── shared UI from SmartPOS ──
  openForm({ title, desc, tone = 'tone-held', icon = 'info', fields = [], submit = 'Confirm', danger = false }) {
    this.closeModal(null);
    const modal = $('#form-modal');
    $('#form-modal-title').textContent = title;
    $('#form-modal-desc').textContent = desc || '';
    const tile = $('#form-modal-tile');
    tile.className = `tile ${tone}`;
    tile.innerHTML = ic(icon, 20);

    $('#form-modal-fields').innerHTML = fields.map((f, i) => `
      <div class="field">
        <label for="ff-${i}">${esc(f.label)}</label>
        ${f.type === 'textarea'
          ? `<textarea class="textarea" id="ff-${i}" placeholder="${esc(f.placeholder || '')}"${f.required ? ' required' : ''}>${esc(f.value || '')}</textarea>`
          : `<input class="input" id="ff-${i}" type="text" value="${esc(f.value || '')}" placeholder="${esc(f.placeholder || '')}"${f.required ? ' required' : ''}>`}
      </div>`).join('');

    const submitBtn = $('#form-modal-submit');
    submitBtn.textContent = submit;
    submitBtn.classList.toggle('is-danger', danger);

    modal.classList.add('open');
    modal.setAttribute('aria-hidden', 'false');
    setTimeout(() => $('#ff-0')?.focus(), 50);

    return new Promise((resolve) => {
      this.modalResolve = resolve;
      $('#form-modal-form').onsubmit = (e) => {
        e.preventDefault();
        const values = fields.map((f, i) => $(`#ff-${i}`).value.trim());
        const missing = fields.findIndex((f, i) => f.required && !values[i]);
        if (missing >= 0) { $(`#ff-${missing}`).focus(); return; }
        this.closeModal(values);
      };
    });
  },

  closeModal(values) {
    const modal = $('#form-modal');
    if (!modal.classList.contains('open')) return;
    modal.classList.remove('open');
    modal.setAttribute('aria-hidden', 'true');
    const resolve = this.modalResolve;
    this.modalResolve = null;
    if (resolve) resolve(values);
  },

  closeMenu() {
    $('#account').classList.remove('open');
    $('#account-btn').setAttribute('aria-expanded', 'false');
  },

  toggleTheme() {
    const root = document.documentElement;
    const current = root.getAttribute('data-theme')
      || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
    const next = current === 'dark' ? 'light' : 'dark';
    root.setAttribute('data-theme', next);
    store.set('theme', next);
  },

  toast(message, type = 'info') {
    const icons = { success: 'circle-check', error: 'circle-alert', warning: 'triangle-alert', info: 'info' };
    const container = $('#toast-container');
    while (container.children.length >= 4) container.firstChild.remove();

    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.setAttribute('role', type === 'error' ? 'alert' : 'status');
    toast.innerHTML = `
      <span class="toast-icon">${ic(icons[type] || 'info', 17)}</span>
      <div class="toast-msg">${esc(message)}</div>
      <button class="toast-close" aria-label="Dismiss">${ic('x', 14)}</button>`;
    const remove = () => {
      toast.classList.add('leaving');
      setTimeout(() => toast.remove(), 250);
    };
    toast.querySelector('.toast-close').addEventListener('click', remove);
    container.appendChild(toast);
    setTimeout(remove, type === 'error' ? 7000 : 4500);
  }
};

document.addEventListener('DOMContentLoaded', () => App.init());
