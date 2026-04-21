(function () {
  'use strict';

  const state = {
    pending: [],
    retry: [],
    dead: [],
    dispatched: [],
    throughput: []
  };

  const EVENT_NAMES = [
    'MessageQueued',
    'MessageDispatched',
    'MessageFailed',
    'MessageDeadLettered',
    'MessageRequeued',
    'MessageCancelled'
  ];

  function esc(s) {
    return String(s).replace(/[&<>"']/g, c => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      '"': '&quot;',
      "'": '&#39;'
    }[c]));
  }

  function relativeTime(iso) {
    if (!iso) return '';
    const t = new Date(iso).getTime();
    if (Number.isNaN(t)) return '';
    const diff = Math.max(0, (Date.now() - t) / 1000);
    if (diff < 60) return Math.floor(diff) + 's ago';
    if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
    if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
    return Math.floor(diff / 86400) + 'd ago';
  }

  async function loadInitial() {
    try {
      const snap = await fetch('api/snapshot').then(r => r.json());
      applySnapshot(snap);
      const thru = await fetch('api/throughput?windowMinutes=60').then(r => r.json());
      state.throughput = Array.isArray(thru) ? thru : [];
      renderAll();
    } catch (err) {
      console.error('Initial load failed', err);
    }
  }

  function applySnapshot(snap) {
    if (!snap) return;
    state.pending = snap.pending || [];
    state.retry = snap.retryQueue || [];
    state.dead = snap.deadLettered || [];
    state.dispatched = snap.dispatched || [];
  }

  function renderAll() {
    document.getElementById('count-pending').textContent = state.pending.length;
    document.getElementById('count-retry').textContent = state.retry.length;
    document.getElementById('count-dead').textContent = state.dead.length;
    renderTab('pending', state.pending, ['force-dispatch', 'cancel']);
    renderTab('retry', state.retry, ['force-dispatch', 'cancel']);
    renderTab('dead', state.dead, ['requeue', 'cancel']);
    renderTab('dispatched', state.dispatched, []);
    renderChart();
  }

  function renderTab(name, rows, actions) {
    const table = document.querySelector('#tab-' + name + ' table');
    if (!table) return;
    const header = '<tr><th>Type</th><th>Id</th><th>Created</th><th>Retry</th><th></th></tr>';
    if (!rows || rows.length === 0) {
      table.innerHTML = header +
        '<tr><td colspan="5" style="color:#777;padding:12px 8px">No messages.</td></tr>';
      return;
    }
    const body = rows.map(r => {
      const id = r.id || '';
      const shortId = id.length > 8 ? id.substring(0, 8) : id;
      const created = r.createdAt || r.enqueuedAt || r.dispatchedAt || r.lastAttemptAt || '';
      const retry = r.retryCount || r.attemptCount || 0;
      const btns = actions
        .map(a => '<button type="button" data-id="' + esc(id) + '" data-action="' + a + '">' + a + '</button>')
        .join('');
      return '<tr>' +
        '<td>' + esc(r.typeName || '') + '</td>' +
        '<td title="' + esc(id) + '">' + esc(shortId) + '</td>' +
        '<td>' + esc(relativeTime(created)) + '</td>' +
        '<td>' + (retry || '') + '</td>' +
        '<td>' + btns + '</td>' +
        '</tr>';
    }).join('');
    table.innerHTML = header + body;
  }

  function renderChart() {
    const svg = document.getElementById('throughput-chart');
    if (!svg) return;
    const points = state.throughput || [];
    if (points.length === 0) {
      svg.innerHTML = '<text x="10" y="50" fill="#777" font-size="11">No throughput data yet</text>';
      return;
    }
    const W = 600, H = 100, PAD = 10;
    const maxY = Math.max(1, ...points.map(p => Math.max(p.dispatched || 0, p.failed || 0)));
    const scaleX = (i) => PAD + (points.length === 1 ? (W - 2 * PAD) / 2 : (i / (points.length - 1)) * (W - 2 * PAD));
    const scaleY = (v) => H - PAD - ((v || 0) / maxY) * (H - 2 * PAD);
    const dispatched = points.map((p, i) => scaleX(i) + ',' + scaleY(p.dispatched)).join(' ');
    const failed = points.map((p, i) => scaleX(i) + ',' + scaleY(p.failed)).join(' ');
    svg.innerHTML =
      '<polyline points="' + dispatched + '" fill="none" stroke="#4a7" stroke-width="2"/>' +
      '<polyline points="' + failed + '" fill="none" stroke="#a44" stroke-width="2"/>' +
      '<text x="10" y="15" fill="#4a7" font-size="10">dispatched (max ' + maxY + ')</text>' +
      '<text x="140" y="15" fill="#a44" font-size="10">failed</text>';
  }

  // Action button delegation.
  document.addEventListener('click', async (e) => {
    const btn = e.target.closest('button[data-action]');
    if (!btn) return;
    const id = btn.dataset.id;
    const action = btn.dataset.action;
    try {
      const res = await fetch('api/messages/' + encodeURIComponent(id) + '/' + action, { method: 'POST' });
      if (res.status === 422) {
        const body = await res.json().catch(() => ({}));
        alert('Action rejected: ' + (body.error || 'invalid state'));
      } else if (!res.ok && res.status !== 204) {
        alert('HTTP ' + res.status);
      }
    } catch (err) {
      alert('Request failed: ' + err.message);
    }
  });

  // Tab switching.
  const tabBar = document.querySelector('.tab-bar');
  if (tabBar) {
    tabBar.addEventListener('click', (e) => {
      const b = e.target.closest('button[data-tab]');
      if (!b) return;
      document.querySelectorAll('.tab-bar button').forEach(x => x.classList.remove('active'));
      document.querySelectorAll('.tab-panel').forEach(x => x.classList.remove('active'));
      b.classList.add('active');
      const panel = document.getElementById('tab-' + b.dataset.tab);
      if (panel) panel.classList.add('active');
    });
  }

  // SSE.
  function setIndicator(cls, text) {
    const el = document.getElementById('sse-indicator');
    if (!el) return;
    el.className = cls;
    el.innerHTML = '&#9679; ' + text;
  }

  function reloadSnapshotAndRender() {
    fetch('api/snapshot')
      .then(r => r.json())
      .then(snap => { applySnapshot(snap); renderAll(); })
      .catch(err => console.error('Snapshot reload failed', err));
  }

  let sse;
  function connectSse() {
    try {
      sse = new EventSource('api/events');
      sse.onopen = () => setIndicator('live', 'live');
      sse.onerror = () => setIndicator('offline', 'offline');
      EVENT_NAMES.forEach(name => {
        sse.addEventListener(name, () => {
          reloadSnapshotAndRender();
          if (name === 'MessageDispatched' || name === 'MessageDeadLettered' || name === 'MessageFailed') {
            // Refresh throughput buffer lazily — a future optimisation could patch locally.
            fetch('api/throughput?windowMinutes=60')
              .then(r => r.json())
              .then(thru => { state.throughput = Array.isArray(thru) ? thru : []; renderChart(); })
              .catch(() => { /* ignore */ });
          }
        });
      });
    } catch (err) {
      setIndicator('offline', 'offline');
      console.error('SSE connect failed', err);
    }
  }

  loadInitial();
  connectSse();
})();
