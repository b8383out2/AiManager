(() => {
  'use strict';

  const app = document.getElementById('app');
  const mq = window.matchMedia('(prefers-color-scheme: dark)');

  const S = {
    payload: null,          // last /api/state response
    index: new Map(),       // session key → session (with _root, _parent, _depth, _badge)
    details: {},            // session key → /api/session response
    view: 'overview',
    rootKey: null,          // the dispatching session being looked at
    selKey: null,           // the highlighted session (root or one of its children)
    decisionId: null,
    drafts: {}, multi: {}, later: [], answered: new Set(),
    openReturns: {}, openMsg: {}, openProc: {}, zoom: null,
    filter: 'all', range: 'all', search: '',
    feed: [], lastAction: new Map(),
    settingsOpen: false, toast: null, scrollThread: true,
    theme: 'system', lang: 'zh', settingsLoaded: false,
    skew: 0, offline: false,
  };

  // ------------------------------------------------------------------ helpers

  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const pad = (n) => String(n).padStart(2, '0');
  const now = () => Date.now() + S.skew;

  function t(key, vars) {
    const dict = window.I18N[S.lang] || window.I18N.zh;
    let s = dict[key] ?? window.I18N.zh[key] ?? key;
    if (vars) for (const k in vars) s = s.split('{' + k + '}').join(String(vars[k]));
    return s;
  }

  function fmtClock(ms) {
    const d = new Date(ms);
    return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  }
  function fmtTime(iso, withSeconds) {
    if (!iso) return '';
    const d = new Date(iso);
    const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}` + (withSeconds ? `:${pad(d.getSeconds())}` : '');
    return d.toDateString() === new Date(now()).toDateString() ? hm : `${d.getMonth() + 1}-${d.getDate()} ${hm}`;
  }
  function ago(iso) {
    if (!iso) return '';
    const s = Math.max(0, (now() - Date.parse(iso)) / 1000);
    if (s < 60) return t('justNow');
    if (s < 3600) return t('minAgo', { n: Math.floor(s / 60) });
    if (s < 86400) return t('hourAgo', { n: Math.floor(s / 3600) });
    return t('dayAgo', { n: Math.floor(s / 86400) });
  }
  function dur(sec) {
    sec = Math.max(0, Math.floor(sec));
    const h = Math.floor(sec / 3600), m = Math.floor(sec / 60) % 60, s = sec % 60;
    return h ? `${h}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`;
  }
  const since = (iso) => (iso ? dur((now() - Date.parse(iso)) / 1000) : '');
  const num = (n) => (n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n || 0));
  const firstLine = (s) => (String(s || '').split('\n').map((l) => l.trim()).find((l) => l.length) || '');
  const clip = (s, n) => (s.length > n ? s.slice(0, n) + '…' : s);
  const letter = (i) => (i < 26 ? String.fromCharCode(65 + i) : String(i + 1));

  const PATHS = {
    logo: '<circle cx="12" cy="5" r="2.5"/><circle cx="5" cy="19" r="2.5"/><circle cx="19" cy="19" r="2.5"/><path d="M12 7.5V12M5 16.5V12h14v4.5"/>',
    spin: '<path d="M21 12a9 9 0 1 1-9-9"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    pause: '<path d="M9 6v12M15 6v12"/>',
    x: '<path d="M7 7l10 10M17 7L7 17"/>',
    minus: '<path d="M6 12h12"/>',
    stop: '<rect x="7" y="7" width="10" height="10" rx="1.5"/>',
    chev: '<path d="M6 9l6 6 6-6"/>',
    right: '<path d="M9 6l6 6-6 6"/>',
    left: '<path d="M15 6l-6 6 6 6"/>',
    search: '<path d="M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM20 20l-3.5-3.5"/>',
    inbox: '<path d="M4 13l2.5-7h11L20 13v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1zM4 13h4.5a3.5 3.5 0 0 0 7 0H20"/>',
    gear: '<path d="M4 7h10M18 7h2M4 17h2M10 17h10"/><circle cx="16" cy="7" r="2"/><circle cx="8" cy="17" r="2"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6L7 7M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4"/>',
    moon: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V6a2 2 0 0 1 2-2h9"/>',
    warn: '<path d="M12 4l9 16H3zM12 10v4M12 17h.01"/>',
    file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>',
  };
  const icon = (name, cls) => `<svg class="ic ${cls || ''}" viewBox="0 0 24 24" aria-hidden="true">${PATHS[name] || ''}</svg>`;

  const ST_ICON = { running: 'spin', waiting: 'pause', idle: 'minus', done: 'check', failed: 'x', ended: 'stop', expired: 'stop' };
  const stIcon = (st) => icon(ST_ICON[st] || 'minus', st === 'running' ? 'spinning' : '');
  const pill = (st, label) => `<span class="pill st-${st}">${stIcon(st)}<span>${esc(label ?? t('st.' + st))}</span></span>`;
  const badge = (s, small) => `<span class="badge ${s && !s._parent ? 'main' : ''} ${small ? 'sm' : ''}">${esc(s ? (s._parent ? s._badge : t('mainBadge')) : '·')}</span>`;

  // Images are read back out of the transcript (or the sent file) by the local server on demand.
  const q = encodeURIComponent;
  const imgUrl = (key, id) => `/api/image?key=${q(key)}&id=${q(id)}&t=${q(window.AIM_TOKEN)}`;
  const fileUrl = (key, id) => `/api/file?key=${q(key)}&id=${q(id)}&t=${q(window.AIM_TOKEN)}`;
  // Not loading="lazy": nodes built through <template> and moved into the page never start a lazy load.
  // The server marks these responses cacheable, so each image is only read once.
  const thumbs = (urls, cls) => (urls.length
    ? `<div class="imgs ${cls || ''}">${urls.map((u) => `<button class="thumb" data-key="${esc(u)}" data-act="zoom" data-src="${esc(u)}" aria-label="${esc(t('openImage'))}"><img src="${esc(u)}" alt="${esc(t('imageUnavailable'))}" decoding="async"></button>`).join('')}</div>`
    : '');

  /** Minimal, escaped-first rendering of the markdown that sessions write. */
  function md(text) {
    return String(text).split('```').map((block, i) => {
      if (i % 2) return `<pre>${esc(block.replace(/^[A-Za-z0-9_+-]*\n/, ''))}</pre>`;
      let h = esc(block);
      h = h.replace(/`([^`\n]+)`/g, '<code>$1</code>');
      h = h.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
      h = h.replace(/^#{1,6}\s+(.+)$/gm, '<strong>$1</strong>');
      h = h.replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>');
      return h.replace(/\n{3,}/g, '\n\n').replace(/\n/g, '<br>');
    }).join('');
  }

  // ------------------------------------------------------------------ DOM patching
  // Rendering builds an HTML string and patches it into the live DOM, so focus, scroll
  // position and text selection survive the once-a-second refresh.

  const keyOf = (n) => (n.nodeType === 1 ? n.getAttribute('data-key') : null);
  const compatible = (a, b) => a.nodeType === b.nodeType && (a.nodeType !== 1 || (a.tagName === b.tagName && keyOf(a) === keyOf(b)));

  function morphNode(from, to) {
    if (from.nodeType !== 1) {
      if (from.nodeValue !== to.nodeValue) from.nodeValue = to.nodeValue;
      return;
    }
    for (let i = from.attributes.length - 1; i >= 0; i--) {
      const name = from.attributes[i].name;
      if (!to.hasAttribute(name)) from.removeAttribute(name);
    }
    for (const a of to.attributes) if (from.getAttribute(a.name) !== a.value) from.setAttribute(a.name, a.value);
    if (from.tagName === 'INPUT' || from.tagName === 'TEXTAREA') {
      // Never overwrite what the user is typing.
      const value = to.getAttribute('value') ?? '';
      if (document.activeElement !== from && from.value !== value) from.value = value;
      return;
    }
    morphChildren(from, to);
  }

  function morphChildren(from, to) {
    let keyed = null;
    let cur = from.firstChild;
    let next = to.firstChild;
    while (next) {
      const node = next;
      next = node.nextSibling;
      let match = null;
      if (cur && compatible(cur, node)) match = cur;
      else if (keyOf(node) != null) {
        if (!keyed) {
          keyed = new Map();
          for (let c = cur; c; c = c.nextSibling) if (keyOf(c) != null) keyed.set(keyOf(c), c);
        }
        const found = keyed.get(keyOf(node));
        if (found && found.parentNode === from && found.tagName === node.tagName) match = found;
      }
      if (match) {
        if (match === cur) cur = cur.nextSibling;
        else from.insertBefore(match, cur);
        morphNode(match, node);
      } else {
        from.insertBefore(node, cur);
      }
    }
    while (cur) {
      const dead = cur;
      cur = cur.nextSibling;
      from.removeChild(dead);
    }
  }

  function patch(root, html) {
    const tpl = document.createElement('template');
    tpl.innerHTML = html;
    morphChildren(root, tpl.content);
  }

  // ------------------------------------------------------------------ data

  async function getJSON(url) {
    const r = await fetch(url, { cache: 'no-store' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    return r.json();
  }
  async function post(url, body) {
    const r = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-AiManager-Token': window.AIM_TOKEN },
      body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const text = await r.text();
    return text ? JSON.parse(text) : null;
  }

  function buildIndex() {
    S.index = new Map();
    for (const root of S.payload.state.roots) {
      let i = 0;
      root._family = [];
      const walk = (s, parent, depth) => {
        s._root = root;
        s._parent = parent;
        s._depth = depth;
        s._badge = parent ? letter(i++) : '';
        if (parent) root._family.push(s);
        S.index.set(s.key, s);
        (s.children || []).forEach((c) => walk(c, s, depth + 1));
      };
      walk(root, null, 0);
    }
  }

  function resetFeed() {
    S.feed = [];
    S.lastAction = new Map();
  }

  function pickDefaults() {
    const roots = S.payload.state.roots;
    const cur = S.rootKey ? S.index.get(S.rootKey) : null;
    if (cur && cur._parent) S.rootKey = cur._root.key;          // it turned out to be someone's child
    if (!S.rootKey || !S.index.has(S.rootKey)) {
      const best = roots.find((r) => (r.status === 'running' || r.status === 'waiting') && r.children.length)
        || roots.find((r) => r.children.length) || roots[0];
      S.rootKey = best ? best.key : null;
      S.selKey = S.rootKey;
      S.scrollThread = true;
      resetFeed();
    }
    if (!S.selKey || !S.index.has(S.selKey)) S.selKey = S.rootKey;
  }

  async function loadDetails() {
    const want = new Set();
    if (S.rootKey && S.view !== 'session') want.add(S.rootKey);
    if (S.selKey && S.view === 'session') want.add(S.selKey);
    for (const k of Object.keys(S.details)) if (!want.has(k)) delete S.details[k];
    await Promise.all([...want].map(async (key) => {
      if (key.includes('~ref')) return;                         // a child with no transcript of its own
      try { S.details[key] = await getJSON('/api/session?key=' + encodeURIComponent(key)); } catch { /* keep the old one */ }
    }));
  }

  /** The live feed is assembled here from changes in what each session is doing. */
  function updateFeed() {
    const root = S.index.get(S.rootKey);
    if (!root) return;
    for (const s of [root, ...root._family]) {
      if (s.status !== 'running' || !s.action) continue;
      const sig = `${s.action.cat}|${s.action.tool}|${s.action.text}`;
      if (S.lastAction.get(s.key) === sig) continue;
      S.lastAction.set(s.key, sig);
      S.feed.unshift({ at: now(), key: s.key, cat: s.action.cat, tool: s.action.tool, text: s.action.text });
    }
    if (S.feed.length > 12) S.feed.length = 12;
  }

  let refreshing = false, again = false;
  async function refresh() {
    if (refreshing) { again = true; return; }
    refreshing = true;
    try {
      const p = await getJSON('/api/state');
      S.payload = p;
      S.offline = false;
      S.skew = Date.parse(p.state.now) - Date.now();
      if (!S.settingsLoaded) {
        S.theme = p.settings.theme;
        S.lang = p.settings.lang;
        S.settingsLoaded = true;
        applyTheme();
      }
      for (const id of [...S.answered]) {
        const d = p.state.decisions.find((x) => x.id === id);
        if (!d || d.status !== 'pending') S.answered.delete(id);
      }
      buildIndex();
      pickDefaults();
      updateFeed();
      await loadDetails();
    } catch {
      S.offline = true;
    } finally {
      refreshing = false;
    }
    render();
    if (again) { again = false; refresh(); }
  }

  const kick = () => { render(); refresh(); };

  function toast(text, kind) {
    S.toast = { text, kind: kind || 'ok' };
    render();
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => { S.toast = null; render(); }, 3200);
  }

  const resolvedTheme = () => (S.theme === 'system' ? (mq.matches ? 'dark' : 'light') : S.theme);
  function applyTheme() {
    const theme = resolvedTheme();
    document.documentElement.dataset.theme = theme;
    document.documentElement.lang = S.lang === 'ko' ? 'ko' : 'zh-CN';
    document.title = t('appName');
    try { window.chrome?.webview?.postMessage({ theme }); } catch { /* not running inside the app window */ }
  }

  function saveSettings(change) {
    post('/api/settings', change).then(refresh).catch(() => toast(t('sendFailed'), 'bad'));
  }

  // ------------------------------------------------------------------ decisions

  function pendingList() {
    const all = S.payload.state.decisions.filter((d) => d.status === 'pending' && !S.answered.has(d.id));
    const deferred = S.later.map((id) => all.find((d) => d.id === id)).filter(Boolean);
    return all.filter((d) => !deferred.includes(d)).concat(deferred);
  }
  const currentDecision = (list) => list.find((d) => d.id === S.decisionId) || list[0] || null;
  const decisionById = (id) => S.payload.state.decisions.find((d) => d.id === id);

  function optionLabel(o) {
    if (o.key === 'allow' || o.key === 'deny') return t(o.key);
    if (o.key && o.key.startsWith('app:')) return t('appAction.' + o.key.slice(4));
    return o.label;
  }
  /** What is actually sent to the session for an option. */
  const optionValue = (o) => (o.key === 'allow' || o.key === 'deny' ? o.key : optionLabel(o));

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      toast(t('copied'), 'ok');
    } catch {
      toast(t('sendFailed'), 'bad');
    }
  }

  async function answer(id, text, how) {
    const d = decisionById(id);
    if (!d || !text) return;
    if (!d.held) { copyText(text); return; }        // nothing is parked on it, so it cannot be delivered
    try {
      const r = await post('/api/answer', { id, text, how });
      if (r.result === 'Delivered' || r.result === 'Queued') {
        S.answered.add(id);
        S.decisionId = null;
        delete S.drafts[id];
        delete S.multi[id];
        toast(t(r.result === 'Delivered' ? 'delivered' : 'queued'), 'ok');
      } else {
        toast(t('notDeliverable'), 'bad');
      }
    } catch {
      toast(t('sendFailed'), 'bad');
    }
    refresh();
  }

  // ------------------------------------------------------------------ shared pieces

  function liveClock() {
    const loaded = S.payload.state.loaded;
    return `<div class="live ${S.offline ? 'off' : ''}"><span class="dot"></span><span>${esc(loaded ? t('live') : t('loading'))}</span><span class="mono">${fmtClock(now())}</span></div>`;
  }

  function actionText(s) {
    if (s.status === 'running') return s.action ? `${t('cat.' + s.action.cat)} ${s.action.text || s.action.tool}` : t('thinking');
    if (s.status === 'waiting') return s.attention && !s.pending ? t('attention') : t('waitingForYou');
    if (s.status === 'failed') return s.error || t('st.failed');
    if (s.status === 'done') return s.returnedAt ? t('returnedAt', { t: fmtTime(s.returnedAt) }) : t('finished');
    if (s.status === 'idle') return t('turnEnded', { t: ago(s.lastAt) });
    return t('endedAgo', { t: ago(s.lastAt) });
  }

  const famPending = (r) => r.pending + r._family.reduce((n, c) => n + (c.pending || 0), 0);

  function tab(view, label, count) {
    const on = S.view === view;
    return `<button class="tab ${on ? 'on' : ''}" data-act="view" data-v="${view}" ${on ? 'aria-current="page"' : ''}>${esc(label)}${count ? `<span class="count">${count}</span>` : ''}</button>`;
  }

  function rootToolbar(root) {
    const counts = {};
    for (const c of root._family) counts[c.status] = (counts[c.status] || 0) + 1;
    return `<div class="toolbar">
  <div class="tb-title"><strong class="ell">${esc(root.title || t('untitled'))}</strong>
    <span class="muted ell">${esc([t('mainSession'), root.project, t('startedAt', { t: fmtTime(root.startedAt) }), root._family.length ? t('childCount', { n: root._family.length }) : ''].filter(Boolean).join(' · '))}</span></div>
  <div class="grow"></div>
  <div class="pills">${root._family.length
    ? ['running', 'waiting', 'failed', 'done'].filter((k) => counts[k]).map((k) => `<span class="pill st-${k}"><span>${esc(t('st.' + k))} ${counts[k]}</span></span>`).join('')
    : pill(root.status)}</div>
  ${liveClock()}
</div>`;
  }

  const blank = (text) => `<div class="blank">${esc(text)}</div>`;

  // ------------------------------------------------------------------ overview

  function viewOverview() {
    const root = S.index.get(S.rootKey);
    if (!root) return `<div class="toolbar"><div class="grow"></div>${liveClock()}</div>${blank(S.payload.state.loaded ? t('noSessions') : t('loading'))}`;
    return `${rootToolbar(root)}
<div class="panes">
  <aside class="pane tree" aria-label="${esc(t('recent'))}">${tree()}</aside>
  <main class="pane thread" id="thread">${threadPane(root, S.details[root.key])}</main>
  <aside class="pane board" aria-label="${esc(t('boardTitle'))}">${board(root)}</aside>
</div>`;
  }

  function tree() {
    const q = S.search.trim().toLowerCase();
    const hit = (s) => (s.title || '').toLowerCase().includes(q) || (s.project || '').toLowerCase().includes(q);
    const roots = S.payload.state.roots.filter((r) => !q || hit(r) || r._family.some(hit));
    return `<div class="tree-search"><label class="search">${icon('search')}<input type="search" data-input="search" value="${esc(S.search)}" placeholder="${esc(t('searchSessions'))}" aria-label="${esc(t('searchSessions'))}"></label></div>
<div class="tree-h">${esc(t('recent'))}</div>
<div class="tree-list">${roots.length ? roots.map(treeRoot).join('') : `<div class="tree-empty">${esc(q ? t('noMatch') : t('noSessions'))}</div>`}</div>
<div class="grow"></div>
<div class="tree-foot">${esc(t('treeFooter'))}</div>`;
  }

  function treeRoot(r) {
    const open = r.key === S.rootKey;
    const pending = famPending(r);
    const meta = [r.project, ago(r.lastAt), r._family.length ? t('childCount', { n: r._family.length }) : ''].filter(Boolean).join(' · ');
    return `<div class="tnode" data-key="${esc(r.key)}">
  <button class="trow root ${open ? 'open' : ''} ${S.selKey === r.key ? 'sel' : ''}" data-act="root" data-id="${esc(r.key)}" aria-expanded="${open}">
    <span class="badge main">${esc(t('mainBadge'))}</span>
    <span class="tcol"><span class="ttitle ell">${esc(r.title || t('untitled'))}</span><span class="tmeta ell">${esc(meta)}</span></span>
    ${pending ? `<span class="count" title="${esc(t('tabDecisions'))}">${pending}</span>` : `<span class="mark st-${r.status}" title="${esc(t('st.' + r.status))}">${stIcon(r.status)}</span>`}
  </button>
  ${open && r._family.length ? `<div class="tkids">${r._family.map(treeKid).join('')}</div>` : ''}
</div>`;
  }

  function treeKid(c) {
    return `<button class="trow kid ${S.selKey === c.key ? 'sel' : ''}" data-key="${esc(c.key)}" style="margin-left:${(c._depth - 1) * 14}px" data-act="select" data-id="${esc(c.key)}" aria-pressed="${S.selKey === c.key}">
  ${badge(c)}<span class="ttitle ell">${esc(c.title || t('untitled'))}</span>
  <span class="tstat st-${c.status}">${stIcon(c.status)}<span>${esc(t('st.' + c.status))}</span></span>
</button>`;
  }

  function threadPane(root, detail) {
    const head = `<div class="pane-h"><h1>${esc(t('threadTitle'))}</h1><span class="muted ell">${esc(t('threadHint'))}</span><div class="grow"></div>
  <button class="btn ghost" data-act="open" data-id="${esc(root.key)}"><span>${esc(t('tabSession'))}</span>${icon('right')}</button></div>`;
    if (!detail) return head + blank(t('loading'));
    if (!detail.conversation.length) return head + blank(t('threadEmpty'));

    const key = detail.session.key;
    const running = detail.session.status === 'running';
    const returns = new Map(detail.returns.map((r) => [r.id, r]));
    let html = '';
    let group = [];
    let proc = [];
    const flushReturns = () => {
      if (!group.length) return;
      html += `<section class="returns" aria-label="${esc(t('returnsTitle'))}">
  <div class="returns-h">${icon('inbox')}<h2>${esc(t('returnsTitle'))}</h2><span class="muted">${esc(t('returnsCount', { n: group.length }))}</span></div>
  ${group.map((r) => returnRow(r, root)).join('')}
</section>`;
      group = [];
    };
    // Thinking and what the session says on its way to an answer are folded together, small and
    // collapsed; the reply that closes a turn is shown in full.
    const flushProc = (live) => {
      if (!proc.length) return;
      html += processBlock(proc, live);
      proc = [];
    };
    for (const item of detail.conversation) {
      if (item.role === 'return') {
        flushProc(false);
        const r = returns.get(item.refId);
        if (r) group.push(r);
        continue;
      }
      flushReturns();
      if (item.role === 'thinking' || (item.role === 'assistant' && !item.final)) {
        proc.push(item);
        continue;
      }
      flushProc(false);
      html += item.role === 'files' ? filesMessage(item, key) : message(item, key);
    }
    flushReturns();
    flushProc(running);

    const pending = pendingList().filter((d) => d.rootKey === root.key).length;
    return `${head}<div class="msgs">${html}</div>${pending ? `<div class="thread-foot"><span class="chip-wait">${esc(t('pendingHere', { n: pending }))}</span><div class="grow"></div><button class="btn dark" data-act="view" data-v="decisions">${esc(t('handle'))}</button></div>` : ''}`;
  }

  function message(item, key) {
    const mine = item.role === 'user';
    const id = `${item.at}|${item.role}|${item.text.length}`;
    const long = item.text.length > 900;
    const open = !!S.openMsg[id];
    return `<div class="msg" data-key="m${esc(id)}">
  <span class="avatar ${mine ? '' : 'main'}">${esc(mine ? t('you') : t('mainBadge'))}</span>
  <div class="msg-b">
    <div class="msg-h"><strong>${esc(mine ? t('you') : t('mainSession'))}</strong><span class="muted">${esc(fmtTime(item.at))}</span></div>
    ${item.text ? `<div class="md">${md(long && !open ? item.text.slice(0, 700) + '…' : item.text)}</div>` : ''}
    ${long ? `<button class="link" data-act="toggleMsg" data-id="${esc(id)}">${esc(open ? t('showLess') : t('showMore'))}</button>` : ''}
    ${item.images ? thumbs(item.images.map((img) => imgUrl(key, img))) : ''}
  </div>
</div>`;
  }

  function filesMessage(item, key) {
    const images = item.files.filter((f) => f.image);
    const others = item.files.filter((f) => !f.image);
    return `<div class="msg" data-key="${esc('f' + item.at)}">
  <span class="avatar main">${esc(t('mainBadge'))}</span>
  <div class="msg-b">
    <div class="msg-h"><strong>${esc(t('mainSession'))}</strong><span class="muted">${esc(t('sentFiles'))} · ${esc(fmtTime(item.at))}</span></div>
    ${item.text ? `<div class="md sub">${md(item.text)}</div>` : ''}
    ${thumbs(images.map((f) => fileUrl(key, f.id)))}
    ${others.length ? `<div class="chips">${others.map((f) => `<button class="chip" data-act="revealFile" data-sk="${esc(key)}" data-id="${esc(f.id)}" title="${esc(t('openFolder'))}">${icon('file')}<span class="ell">${esc(f.name)}</span></button>`).join('')}</div>` : ''}
  </div>
</div>`;
  }

  function processBlock(items, live) {
    const first = items[0];
    const last = items[items.length - 1];
    const id = `p${first.at}|${first.role}`;
    const open = !!S.openProc[id];
    const notes = items.filter((i) => i.role === 'assistant').length;
    const thoughts = items.length - notes;
    const counts = [notes ? t('procNotes', { n: notes }) : '', thoughts ? t('procThoughts', { n: thoughts }) : ''].filter(Boolean).join(' · ');
    const span = (Date.parse(last.at) - Date.parse(first.at)) / 1000;
    return `<div class="proc" data-key="${esc(id)}">
  <button class="proc-h" data-act="toggleProc" data-id="${esc(id)}" aria-expanded="${open}">
    ${icon('right', open ? 'rot' : '')}<span>${esc(t('procTitle'))}</span><span class="proc-n">${esc(counts)}</span>${span >= 1 ? `<span class="proc-n">${esc(dur(span))}</span>` : ''}${live ? icon('spin', 'spinning') : ''}
  </button>
  ${open ? `<div class="proc-b">${items.map(procItem).join('')}</div>` : live ? `<div class="proc-peek ell">${esc(firstLine(last.text))}</div>` : ''}
</div>`;
  }

  function procItem(i) {
    const thought = i.role === 'thinking';
    return `<div class="proc-i ${thought ? 'thought' : ''}" data-key="${esc(i.at + i.role + i.text.length)}">
  <div class="proc-m"><span class="mono">${esc(fmtTime(i.at, true))}</span><span class="tag">${esc(t(thought ? 'tagThinking' : 'tagNote'))}</span></div>
  <div class="md">${md(i.text)}</div>
</div>`;
  }

  /** Finds which child a report came from so it can carry that child's letter. */
  function childFor(root, r) {
    return root._family.find((c) => c.id && c.id === r.fromKey) || root._family.find((c) => c.title === r.fromLabel) || null;
  }

  function returnRow(r, root) {
    const open = !!S.openReturns[r.id];
    const child = childFor(root, r);
    const status = r.status === 'failed' ? 'failed' : 'done';
    return `<div class="ret ${child && child.key === S.selKey ? 'sel' : ''}" data-key="${esc(r.id)}">
  <button class="ret-h" data-act="toggleReturn" data-id="${esc(r.id)}" aria-expanded="${open}">
    ${badge(child)}<strong class="ell">${esc(r.fromLabel || t('untitled'))}</strong>${pill(status)}
    <span class="grow"></span><span class="muted mono">${esc(fmtTime(r.at))}</span>${icon('chev', open ? 'flip' : '')}
  </button>
  <p class="ret-sum">${esc(clip(firstLine(r.text), 160))}</p>
  ${open ? `<div class="ret-body"><div class="md">${md(r.text)}</div>
    <div class="ret-meta"><span>${esc(t('fullText', { n: r.fullLength }))}</span><span class="grow"></span>${child && child.kind !== 'stub' ? `<button class="link" data-act="open" data-id="${esc(child.key)}">${esc(t('viewSession'))}</button>` : ''}</div></div>` : ''}
</div>`;
  }

  function board(root) {
    const fam = root._family;
    const pending = pendingList();
    return `<div class="pane-h"><h2>${esc(t('boardTitle'))}</h2><span class="muted">${esc(t('boardCount', { n: fam.length }))}</span></div>
${pending.length ? decidePanel(pending) : ''}
<div class="cards">${card(root)}${fam.map(card).join('')}</div>
${fam.length ? '' : `<p class="hint">${esc(t('noChildren'))}</p>`}
<section class="feed">
  <div class="feed-h"><span class="dot"></span><h2>${esc(t('feedTitle'))}</h2><span class="muted ell">${esc(t('feedHint'))}</span></div>
  ${S.feed.length ? S.feed.map((f) => `<div class="feed-r" data-key="${esc(f.at + f.key)}"><span class="mono muted">${fmtClock(f.at)}</span>${badge(S.index.get(f.key), true)}<span class="verb">${esc(t('cat.' + f.cat))}</span><span class="mono ell">${esc(f.text || f.tool)}</span></div>`).join('') : `<div class="feed-empty">${esc(t('feedEmpty'))}</div>`}
</section>`;
  }

  function decidePanel(pending) {
    return `<section class="decide" aria-label="${esc(t('decideTitle'))}">
  <div class="decide-h"><h2>${esc(t('decideTitle'))}</h2><span class="count">${pending.length}</span><span class="muted ell">${esc(t('decideHint'))}</span><div class="grow"></div>
    <button class="btn dark" data-act="view" data-v="decisions">${esc(t('decideGo'))}</button></div>
  ${pending.slice(0, 5).map((d, i) => `<button class="decide-r" data-key="${esc(d.id)}" data-act="pickDecision" data-id="${esc(d.id)}">
    <span class="nth">${i + 1}</span><span class="tcol"><span class="ell q">${esc(d.question)}</span><span class="tmeta ell">${esc(d.sessionTitle || t('untitled'))}</span></span><span class="mono muted">${esc(fmtTime(d.raisedAt))}</span></button>`).join('')}
</section>`;
  }

  function card(s) {
    const sel = s.key === S.selKey;
    const pct = s.planTotal ? Math.round((100 * s.planDone) / s.planTotal) : s.status === 'done' ? 100 : 0;
    const right = [s.status === 'running' && s.turnStartedAt ? since(s.turnStartedAt) : ago(s.lastAt), s.tools ? t('toolCalls', { n: s.tools }) : ''].filter(Boolean).join(' · ');
    return `<div class="card ${sel ? 'sel' : ''}" data-key="${esc(s.key)}" data-act="select" data-id="${esc(s.key)}" role="button" tabindex="0" aria-pressed="${sel}">
  <span class="row">${badge(s)}<span class="title ell">${esc(s.title || t('untitled'))}</span>${pill(s.status)}</span>
  <span class="act mono">${esc(actionText(s))}</span>
  <span class="track"><span class="fill st-${s.status}" style="width:${pct}%"></span></span>
  <span class="row stats"><span>${esc(s.planTotal ? t('steps', { a: s.planDone, b: s.planTotal }) : t('kind.' + s.kind))}</span><span class="grow"></span><span>${esc(right)}</span>
    ${s.kind === 'stub' ? '' : `<button class="link" data-act="open" data-id="${esc(s.key)}">${esc(t('tabSession'))}</button>`}</span>
</div>`;
  }

  // ------------------------------------------------------------------ decisions view

  function viewDecisions() {
    const list = pendingList();
    const cur = currentDecision(list);
    const history = S.payload.state.decisions.filter((d) => d.status !== 'pending' || S.answered.has(d.id));
    return `<div class="toolbar">
  <div class="tb-title"><strong>${esc(t('dqTitle'))}</strong><span class="muted ell">${esc(t('dqHint'))}</span></div>
  <div class="grow"></div>
  <div class="pills"><span class="pill st-waiting"><span>${esc(t('dqPending', { n: list.length }))}</span></span><span class="pill st-done"><span>${esc(t('dqDone', { n: history.length }))}</span></span></div>
  ${liveClock()}
</div>
<div class="panes">
  <aside class="pane queue" aria-label="${esc(t('queueTitle'))}">
    <div class="sec-h"><h2>${esc(t('queueTitle'))}</h2><span class="muted">${esc(t('queueHint'))}</span></div>
    ${list.length ? list.map((d, i) => queueItem(d, i, cur)).join('') : `<div class="dashed">${esc(t('queueEmpty'))}</div>`}
    <div class="sec-h"><h2>${esc(t('historyTitle'))}</h2><span class="muted">${esc(t('historyHint'))}</span></div>
    ${history.length ? history.map(historyItem).join('') : `<div class="dashed">${esc(t('historyEmpty'))}</div>`}
  </aside>
  <main class="pane focus">${cur ? focusCard(cur, list) : allDone()}</main>
  <aside class="pane source" aria-label="${esc(t('sourceTitle'))}">${cur ? sourcePanel(cur) : ''}</aside>
</div>`;
  }

  function queueItem(d, i, cur) {
    const on = cur && cur.id === d.id;
    return `<button class="qitem ${on ? 'sel' : ''}" data-key="${esc(d.id)}" data-act="pickDecision" data-id="${esc(d.id)}" aria-pressed="${on}">
  <span class="row"><span class="nth">${i + 1}</span><span class="ell sub">${esc(d.sessionTitle || t('untitled'))}</span><span class="grow"></span><span class="mono muted">${esc(fmtTime(d.raisedAt))}</span></span>
  <span class="q">${esc(clip(d.question, 140))}</span>
  <span class="row muted small"><span>${esc(t('dk.' + d.kind))}</span>${d.held ? '' : `<span class="tag-warn">${esc(t(sessionBusy(d) ? 'tagLater' : 'tagClaude'))}</span>`}</span>
</button>`;
  }

  /** The session went back to work after another answer; this question gets parked again when it stops. */
  const sessionBusy = (d) => (S.index.get(d.sessionKey) || {}).status === 'running';

  function historyItem(d) {
    const how = d.status === 'answered' ? t('youReplied', { how: t('how.' + (d.answerHow || 'native')), text: clip(d.answerText || '', 60) })
      : d.status === 'expired' ? t('wasExpired') : t('wasDismissed');
    return `<div class="hitem" data-key="${esc(d.id)}">
  ${d.status === 'answered' ? icon('check', 'ok') : icon('minus', 'dim')}
  <div class="tcol"><span class="hq">${esc(clip(d.question, 120))}</span><span class="tmeta">${esc(how)} · ${esc(fmtTime(d.answeredAt || d.raisedAt))}</span><span class="tmeta ell">${esc(d.sessionTitle || '')}</span></div>
</div>`;
  }

  function focusCard(d, list) {
    const pos = list.findIndex((x) => x.id === d.id) + 1;
    const integ = S.payload.integration;
    const draft = S.drafts[d.id] || '';
    const picked = S.multi[d.id] || [];
    const options = d.options || [];
    const perm = d.kind === 'permission';

    let choices = '';
    if (d.multiSelect) {
      choices = `<div class="opts">${options.map((o) => {
        const on = picked.includes(o.label);
        return `<button class="opt ${on ? 'on' : ''}" data-act="toggleMulti" data-id="${esc(d.id)}" data-text="${esc(o.label)}" aria-pressed="${on}"><span>${esc(optionLabel(o))}</span>${o.description ? `<small>${esc(o.description)}</small>` : ''}</button>`;
      }).join('')}</div>
      <div class="row gap"><span class="muted small">${esc(t('multiHint'))}</span><span class="grow"></span><button class="btn primary" data-act="submitMulti" data-id="${esc(d.id)}">${esc(t('submitChoice'))}</button></div>`;
    } else if (options.length) {
      choices = `<div class="opts">${options.map((o) => `<button class="opt" data-act="answer" data-id="${esc(d.id)}" data-text="${esc(optionValue(o))}"><span>${esc(optionLabel(o))}</span>${o.description ? `<small>${esc(o.description)}</small>` : ''}</button>`).join('')}</div>`;
    }

    return `${integ.installed || d.held ? '' : `<div class="banner warn">${icon('warn')}<span>${esc(t('needIntegration'))}</span><span class="grow"></span><button class="btn dark" data-act="settings">${esc(t('enableNow'))}</button></div>`}
<div class="focus-top"><h1>${esc(t('posText', { i: pos, n: list.length }))}</h1>
  <div class="dots" aria-hidden="true">${list.map((x) => `<span class="${x.id === d.id ? 'on' : ''}"></span>`).join('')}</div>
  <div class="grow"></div>${d.held ? `<button class="btn" data-act="dismiss" data-id="${esc(d.id)}" title="${esc(t('noReplyHint'))}">${esc(t('noReply'))}</button>` : ''}${list.length > 1 ? `<button class="btn" data-act="later">${esc(t('later'))}</button>` : ''}</div>
<article class="dcard" data-key="${esc(d.id)}">
  <div class="row wrap gap"><strong>${esc(d.sessionTitle || t('untitled'))}</strong><span class="tag">${esc(t('dk.' + d.kind))}</span>
    <span class="grow"></span><span class="muted small">${esc(t('raisedAt', { t: fmtTime(d.raisedAt) }))} · ${esc(t('waited', { t: since(d.raisedAt) }))}${d.held && d.holdUntil ? ' · ' + esc(t('holdLeft', { t: dur((Date.parse(d.holdUntil) - now()) / 1000) })) : ''}</span></div>
  ${d.context ? `<div><div class="label">${esc(t('whatHappened'))}</div><div class="md ctx">${md(clip(d.context, 420))}</div></div>` : ''}
  <div class="ask">
    <div class="label">${esc(t('needDecide'))}${d.header ? ' · ' + esc(d.header) : ''}</div>
    <h2>${esc(perm ? t('dk.permission') + '：' + d.question : d.question)}</h2>
    ${choices}
  </div>
  ${perm ? '' : `<div><label class="label" for="reply">${esc(t('orType'))}</label>
    <div class="row gap wrap"><input id="reply" class="reply" type="text" data-input="draft" data-id="${esc(d.id)}" data-enter="send" value="${esc(draft)}" placeholder="${esc(t('typePlaceholder'))}" autocomplete="off">
      <button class="btn primary" data-act="send" data-id="${esc(d.id)}">${esc(d.held ? t('send') : t('copyReply'))}</button></div></div>`}
  ${d.held
      ? `<p class="note">${esc(t('noteSend', { to: d.sessionTitle || t('untitled') }))}</p>`
      : sessionBusy(d)
        ? `<div class="banner warn col"><strong>${icon('warn')}${esc(t('laterTitle'))}</strong><span>${esc(t('laterBody'))}</span></div>`
        : `<div class="banner warn col"><strong>${icon('warn')}${esc(t('notHeldTitle'))}</strong><span>${esc(t('notHeldBody'))}</span>
          <div class="row gap"><button class="btn" data-act="dismiss" data-id="${esc(d.id)}">${esc(t('markHandled'))}</button></div></div>`}
</article>`;
  }

  function allDone() {
    return `<div class="done-all">${icon('check', 'big')}<h1>${esc(t('allDone'))}</h1><p>${esc(t('allDoneHint'))}</p></div>`;
  }

  function sourcePanel(d) {
    const s = S.index.get(d.sessionKey);
    return `<div class="sec-h"><h2>${esc(t('sourceTitle'))}</h2></div>
${s ? `<div class="scard"><span class="row"><strong class="ell">${esc(s.title || t('untitled'))}</strong><span class="grow"></span>${pill(s.status)}</span>
  <span class="act mono">${esc(actionText(s))}</span>
  ${d.rootTitle && d.rootKey !== d.sessionKey ? `<span class="muted small">${esc(t('mainSession'))}：${esc(d.rootTitle)}</span>` : ''}
  <button class="link" data-act="open" data-id="${esc(s.key)}">${esc(t('viewSession'))}</button></div>` : ''}
${d.context ? `<div class="sec-h"><h2>${esc(t('rawTitle'))}</h2><span class="muted">${esc(t('rawHint'))}</span></div><div class="raw md">${md(d.context)}</div>` : ''}`;
  }

  // ------------------------------------------------------------------ timeline

  const STEPS = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440, 2880];

  function viewTimeline() {
    const root = S.index.get(S.rootKey);
    if (!root) return `<div class="toolbar"><div class="grow"></div>${liveClock()}</div>${blank(t('noSessions'))}`;
    const detail = S.details[root.key];
    const lanes = [];
    const walk = (s) => { lanes.push(s); (s.children || []).forEach(walk); };
    walk(detail ? detail.session : root);

    const end = now();
    const starts = lanes.map((l) => Date.parse(l.startedAt)).filter((x) => !Number.isNaN(x));
    let start = starts.length ? Math.min(...starts) : end - 3600000;
    if (S.range !== 'all') start = Math.max(start, end - Number(S.range) * 60000);
    start = Math.min(start, end - 60000);
    const total = end - start;
    const pos = (ms) => Math.max(0, Math.min(100, ((ms - start) / total) * 100));
    const seg = (a, b, cls) => {
      if (b < start) return '';
      const left = pos(a);
      return `<span class="seg ${cls}" style="left:${left.toFixed(2)}%;width:${Math.max(0.35, pos(b) - left).toFixed(2)}%"></span>`;
    };

    const stepMin = STEPS.find((m) => total / (m * 60000) <= 10) || 2880;
    const stepMs = stepMin * 60000;
    const ticks = [];
    for (let x = Math.ceil(start / stepMs) * stepMs; x <= end; x += stepMs) ticks.push(x);
    const tickLabel = (ms) => {
      const d = new Date(ms);
      const hm = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
      return total > 86400000 ? `${d.getMonth() + 1}-${d.getDate()} ${hm}` : hm;
    };

    const decisions = S.payload.state.decisions;
    const lane = (l) => {
      const me = S.index.get(l.key) || l;
      const spans = l.spans || [];
      let bars = '';
      spans.forEach((sp, i) => {
        const last = i === spans.length - 1;
        const a = Date.parse(sp[0]);
        const b = last && l.status === 'running' ? end : Date.parse(sp[1]);
        bars += seg(a, b, last && l.status === 'running' ? 'run' : last && l.status === 'failed' ? 'fail' : 'past');
      });
      if (!spans.length && l.startedAt) {
        const a = Date.parse(l.startedAt);
        bars += seg(a, l.status === 'running' ? end : Date.parse(l.returnedAt || l.lastAt || l.startedAt), l.status === 'running' ? 'run' : l.status === 'failed' ? 'fail' : 'past');
      }
      if (l.status === 'waiting' && l.lastAt) bars += seg(Date.parse(l.lastAt), end, 'wait');

      let marks = '';
      if (detail && l.key === root.key)
        for (const r of detail.returns) {
          const at = Date.parse(r.at);
          if (at < start) continue;
          marks += `<span class="mk ret ${r.status === 'failed' ? 'bad' : ''}" style="left:${pos(at).toFixed(2)}%" title="${esc(r.fromLabel + ' · ' + fmtTime(r.at))}">${esc((childFor(root, r) || {})._badge || '·')}</span>`;
        }
      for (const d of decisions) {
        if (d.sessionKey !== l.key) continue;
        const at = Date.parse(d.raisedAt);
        if (at < start) continue;
        marks += `<span class="mk ask ${d.status === 'pending' ? 'on' : ''}" style="left:${pos(at).toFixed(2)}%" title="${esc(clip(d.question, 80) + ' · ' + fmtTime(d.raisedAt))}">?</span>`;
      }
      return `<div class="tl-row" data-key="${esc(l.key)}">
  <button class="tl-label ${S.selKey === l.key ? 'sel' : ''}" data-act="select" data-id="${esc(l.key)}" style="padding-left:${8 + (me._depth || 0) * 12}px">${badge(me)}<span class="ell">${esc(l.title || t('untitled'))}</span>${pill(l.status)}</button>
  <div class="tl-track">${bars}${marks}</div>
</div>`;
    };

    const returns = detail ? detail.returns.slice().sort((a, b) => Date.parse(a.at) - Date.parse(b.at)) : [];
    return `${rootToolbar(root)}
<div class="page">
  <section class="panel">
    <div class="panel-h"><h1>${esc(t('tlTitle'))}</h1><span class="muted">${esc(tickLabel(start))} – ${esc(t('nowLabel'))}</span><div class="grow"></div>
      <div class="legend"><span><i class="seg past"></i>${esc(t('legend.past'))}</span><span><i class="seg run"></i>${esc(t('legend.run'))}</span><span><i class="seg wait"></i>${esc(t('legend.wait'))}</span><span><i class="seg fail"></i>${esc(t('legend.fail'))}</span><span><i class="mk ret">A</i>${esc(t('legend.ret'))}</span><span><i class="mk ask on">?</i>${esc(t('legend.ask'))}</span></div>
      <div class="seg-ctl" role="group">${['all', '120', '30'].map((v) => `<button class="${S.range === v ? 'on' : ''}" data-act="range" data-v="${v}" aria-pressed="${S.range === v}">${esc(t('range.' + v))}</button>`).join('')}</div>
    </div>
    <div class="tl-scroll"><div class="tl">
      <div class="tl-row axis"><div></div><div class="tl-track">${ticks.map((x) => `<span class="tick" style="left:${pos(x).toFixed(2)}%">${esc(tickLabel(x))}</span>`).join('')}</div></div>
      <div class="tl-body">
        <div class="tl-grid"><div></div><div class="tl-track">${ticks.map((x) => `<span class="gl" style="left:${pos(x).toFixed(2)}%"></span>`).join('')}</div></div>
        ${lanes.map(lane).join('')}
      </div>
    </div></div>
  </section>
  <section class="panel pad">
    <div class="row gap wrap"><h2>${esc(t('orderTitle'))}</h2><span class="muted">${esc(t('orderHint'))}</span></div>
    ${returns.length ? `<div class="order">${returns.map((r, i) => `<div class="ocard" data-key="${esc(r.id)}"><span class="muted small">${esc(t('nth', { n: i + 1 }))} · ${esc(fmtTime(r.at))}</span>
      <span class="row">${badge(childFor(root, r))}<strong class="ell">${esc(r.fromLabel || t('untitled'))}</strong>${pill(r.status === 'failed' ? 'failed' : 'done')}</span>
      <span class="sub clamp">${esc(clip(firstLine(r.text), 120))}</span></div>`).join('')}</div>` : `<p class="hint">${esc(t('orderEmpty'))}</p>`}
  </section>
</div>`;
  }

  // ------------------------------------------------------------------ session detail

  function viewSession() {
    const s = S.index.get(S.selKey);
    if (!s) return `<div class="toolbar"><div class="grow"></div>${liveClock()}</div>${blank(t('pickSession'))}`;
    const d = S.details[s.key];
    const isRoot = !s._parent;
    const head = `<div class="toolbar">
  <button class="btn ghost" data-act="view" data-v="overview">${icon('left')}<span class="ell">${esc(isRoot ? t('back') : s._root.title || t('back'))}</span></button>
  ${badge(s)}
  <div class="tb-title"><strong class="ell">${esc(s.title || t('untitled'))}</strong>
    <span class="muted ell">${esc([t('kind.' + s.kind), s.agentType && s.kind !== 'stub' ? s.agentType : '', s.project, s.startedAt ? t('startedAt', { t: fmtTime(s.startedAt) }) : '', s.model].filter(Boolean).join(' · '))}</span></div>
  ${pill(s.status)}
  <div class="grow"></div>
  ${s.status === 'running' && s.turnStartedAt ? `<div class="elapsed"><span class="muted">${esc(t('elapsed'))}</span><strong class="mono">${since(s.turnStartedAt)}</strong></div>` : ''}
  ${liveClock()}
</div>`;
    if (s.kind === 'stub') return head + blank(t('noTranscript'));
    if (!d) return head + blank(t('loading'));

    const plan = d.plan || [];
    const done = plan.filter((p) => p.status === 'completed').length;
    const rows = d.activity.filter((a) => S.filter === 'all' || (S.filter === 'tool' ? a.kind === 'tool' : a.kind !== 'tool'));
    const usage = [
      [t('u.tools'), String(s.tools)],
      [t('u.tokens'), num(s.outTokens)],
      [t('u.context'), num(s.ctxTokens)],
    ];
    if (s.costUsd != null) usage.push([t('u.cost'), '$' + s.costUsd.toFixed(2)]);
    if (d.linesAdded || d.linesRemoved) usage.push([t('u.lines'), `+${d.linesAdded} −${d.linesRemoved}`]);

    return `${head}
<div class="panes">
  <aside class="pane side">
    <section><h2>${esc(isRoot ? t('briefTitleMain') : t('briefTitle'))}</h2><div class="brief md">${d.brief ? md(clip(d.brief, 1600)) : esc(t('briefEmpty'))}</div></section>
    <section><div class="row gap"><h2>${esc(t('planTitle'))}</h2>${plan.length ? `<span class="muted">${esc(t('planDone', { a: done, b: plan.length }))}</span>` : ''}</div>
      ${plan.length ? `<span class="track"><span class="fill st-${s.status === 'running' ? 'running' : 'done'}" style="width:${Math.round((100 * done) / plan.length)}%"></span></span>
      <ol class="plan">${plan.map((p) => `<li class="${p.status}">${p.status === 'completed' ? icon('check', 'ok') : p.status === 'in_progress' ? icon('spin', 'spinning') : icon('minus', 'dim')}<span>${esc(p.text)}</span></li>`).join('')}</ol>`
      : `<p class="hint">${esc(t('planEmpty'))}</p>`}</section>
  </aside>
  <main class="pane work">
    <div class="pane-h"><h1>${esc(t('workTitle'))}</h1><span class="muted">${esc(t('workHint', { n: rows.length }))}</span><div class="grow"></div>
      <div class="seg-ctl" role="group">${['all', 'tool', 'say'].map((v) => `<button class="${S.filter === v ? 'on' : ''}" data-act="filter" data-v="${v}" aria-pressed="${S.filter === v}">${esc(t('f.' + v))}</button>`).join('')}</div></div>
    ${rows.length ? `<div class="stream">${rows.map((a) => activityRow(a, s.key)).join('')}</div>` : blank(t('workEmpty'))}
  </main>
  <aside class="pane side">
    <section><h2>${esc(t('filesTitle'))}</h2>${d.files.length ? d.files.map((f) => `<div class="file" title="${esc(f.path)}"><span class="mono ell">${esc(f.short)}</span><span class="muted small">${esc(t('edits', { n: f.edits }))}</span></div>`).join('') : `<p class="hint">${esc(t('filesEmpty'))}</p>`}</section>
    <section><h2>${esc(t('usageTitle'))}</h2><div class="usage">${usage.map(([k, v]) => `<div><span class="muted small">${esc(k)}</span><strong class="mono">${esc(v)}</strong></div>`).join('')}</div></section>
    <section><h2>${esc(t('retTitle'))}</h2><div class="dashed">${esc(isRoot ? t('retMain') : s.returned ? t('retDone', { t: fmtTime(s.returnedAt) }) : t('retPending'))}</div></section>
    <div class="grow"></div>
    ${s.cwd ? `<button class="btn" data-act="reveal" data-id="${esc(s.key)}" title="${esc(s.cwd)}">${icon('folder')}<span>${esc(t('openFolder'))}</span></button>` : ''}
  </aside>
</div>`;
  }

  function activityRow(a, key) {
    const tool = a.kind === 'tool';
    const tag = tool ? t('cat.' + a.cat) : t('tag.' + a.kind);
    const result = a.pending ? `<span class="res run">${icon('spin', 'spinning')}${esc(t('inProgress'))}</span>`
      : a.error ? `<span class="res bad">${esc(t('errorMark'))}</span>`
      : a.seconds ? `<span class="res">${esc(t('seconds', { n: a.seconds }))}</span>` : '';
    return `<div class="arow ${a.pending ? 'now' : ''} ${a.kind}" data-key="${esc(a.at + a.kind + a.tool + a.text.length)}">
  <span class="mono muted at">${esc(fmtTime(a.at, true))}</span><span class="tag">${esc(tag)}</span>
  <div class="atext ${tool ? 'mono' : ''}">${esc(tool ? a.text || a.tool : clip(a.text, 400))}${a.images ? thumbs(a.images.slice(0, 4).map((img) => imgUrl(key, img)), 'small') : ''}</div>${result}
</div>`;
  }

  // ------------------------------------------------------------------ settings

  function settingsModal() {
    const p = S.payload, cfg = p.settings, integ = p.integration;
    const segs = (act, values, current, label) => `<div class="seg-ctl" role="group">${values.map((v) => `<button class="${String(current) === String(v) ? 'on' : ''}" data-act="${act}" data-v="${v}" aria-pressed="${String(current) === String(v)}">${esc(label(v))}</button>`).join('')}</div>`;
    const state = !integ.installed ? t('intOff') : integ.upToDate ? t('intOn') : t('intStale');
    return `<div class="scrim" data-act="closeSettings"></div>
<div class="modal" role="dialog" aria-modal="true" aria-label="${esc(t('sTitle'))}">
  <div class="modal-h"><h1>${esc(t('sTitle'))}</h1><div class="grow"></div><button class="iconbtn" data-act="closeSettings" aria-label="${esc(t('close'))}">${icon('x')}</button></div>
  <div class="modal-b">
    <section><h2>${esc(t('sAppearance'))}</h2>
      <div class="field"><span>${esc(t('sTheme'))}</span>${segs('setTheme', ['light', 'dark', 'system'], S.theme, (v) => t('theme.' + v))}</div>
      <div class="field"><span>${esc(t('sLanguage'))}</span>${segs('lang', ['zh', 'ko'], S.lang, (v) => (v === 'zh' ? '中文' : '한국어'))}</div></section>
    <section><h2>${esc(t('sData'))}</h2>
      <div class="field"><span>${esc(t('sHistory'))}</span>${segs('setDays', [1, 3, 7, 14], cfg.historyDays, (v) => t('days', { n: v }))}</div></section>
    <section><h2>${esc(t('sIntegration'))}</h2>
      <p class="sub">${esc(t('intBody'))}</p>
      <div class="field"><span class="pill st-${integ.installed && integ.upToDate ? 'done' : integ.installed ? 'waiting' : 'ended'}"><span>${esc(state)}</span></span><span class="grow"></span>
        ${integ.installed ? `${integ.upToDate ? '' : `<button class="btn primary" data-act="integration" data-v="on">${esc(t('intReapply'))}</button>`}<button class="btn" data-act="integration" data-v="off">${esc(t('intDisable'))}</button>`
          : `<button class="btn primary" data-act="integration" data-v="on">${esc(t('intEnable'))}</button>`}</div>
      ${S.integrationError ? `<div class="banner warn">${icon('warn')}<span>${esc(t('intError', { e: S.integrationError }))}</span></div>` : ''}
      <div class="field"><span>${esc(t('intHold'))}</span>${segs('setHold', [5, 15, 30, 60], cfg.holdMinutes, (v) => t('minutes', { n: v }))}</div>
      <label class="check"><input type="checkbox" data-change="toggleTrailing" ${cfg.holdTrailingQuestions ? 'checked' : ''}><span>${esc(t('intTrailing'))}</span></label>
      <label class="check"><input type="checkbox" data-change="togglePermission" ${cfg.relayPermissions ? 'checked' : ''}><span>${esc(t('intPermission'))}</span></label>
      <p class="muted small">${esc(t('intNote'))}</p>
      <p class="muted small mono">${esc(t('intFile', { f: integ.settingsFile }))}</p></section>
  </div>
</div>`;
  }

  // ------------------------------------------------------------------ shell + render

  function render() {
    if (!S.payload) {
      patch(app, `<div class="boot">${esc(S.offline ? '…' : t('loading'))}</div>`);
      return;
    }
    const pending = pendingList().length;
    patch(app, `<div class="shell">
<header class="bar">
  <div class="brand">${icon('logo')}<span>${esc(t('appName'))}</span></div>
  <nav class="tabs" aria-label="${esc(t('views'))}">${tab('overview', t('tabOverview'))}${tab('decisions', t('tabDecisions'), pending)}${tab('timeline', t('tabTimeline'))}${tab('session', t('tabSession'))}</nav>
  <div class="grow"></div>
  <div class="seg-ctl onbar" role="group" aria-label="${esc(t('language'))}">
    <button class="${S.lang === 'zh' ? 'on' : ''}" data-act="lang" data-v="zh" aria-pressed="${S.lang === 'zh'}">中文</button>
    <button class="${S.lang === 'ko' ? 'on' : ''}" data-act="lang" data-v="ko" aria-pressed="${S.lang === 'ko'}">한국어</button>
  </div>
  <button class="iconbtn" data-act="theme" aria-label="${esc(t('toggleTheme'))}" title="${esc(t('toggleTheme'))}">${icon(resolvedTheme() === 'dark' ? 'sun' : 'moon')}</button>
  <button class="iconbtn" data-act="settings" aria-label="${esc(t('settings'))}" title="${esc(t('settings'))}">${icon('gear')}</button>
</header>
${S.view === 'decisions' ? viewDecisions() : S.view === 'timeline' ? viewTimeline() : S.view === 'session' ? viewSession() : viewOverview()}
${S.settingsOpen ? settingsModal() : ''}
${S.zoom ? `<div class="lightbox" data-act="unzoom" role="dialog" aria-modal="true" aria-label="${esc(t('openImage'))}"><img src="${esc(S.zoom)}" alt="${esc(t('imageUnavailable'))}"></div>` : ''}
${S.toast ? `<div class="toast ${S.toast.kind}" role="status">${esc(S.toast.text)}</div>` : ''}
</div>`);

    if (S.scrollThread && S.view === 'overview' && S.details[S.rootKey]) {
      const el = document.getElementById('thread');
      if (el) el.scrollTop = el.scrollHeight;
      S.scrollThread = false;
    }
  }

  // ------------------------------------------------------------------ events

  const actions = {
    view(d) { S.view = d.v; if (d.v === 'overview') S.scrollThread = true; kick(); },
    root(d) { S.rootKey = d.id; S.selKey = d.id; S.scrollThread = true; resetFeed(); kick(); },
    select(d) { S.selKey = d.id; render(); },
    open(d) { S.selKey = d.id; S.view = 'session'; kick(); },
    toggleReturn(d) { S.openReturns[d.id] = !S.openReturns[d.id]; render(); },
    toggleMsg(d) { S.openMsg[d.id] = !S.openMsg[d.id]; render(); },
    toggleProc(d) { S.openProc[d.id] = !S.openProc[d.id]; render(); },
    zoom(d) { S.zoom = d.src; render(); },
    unzoom() { S.zoom = null; render(); },
    revealFile(d) { post('/api/reveal-file', { key: d.sk, id: d.id }).catch(() => toast(t('sendFailed'), 'bad')); },
    pickDecision(d) { S.decisionId = d.id; S.view = 'decisions'; render(); },
    later() {
      const cur = currentDecision(pendingList());
      if (!cur) return;
      S.later = S.later.filter((x) => x !== cur.id).concat(cur.id);
      S.decisionId = null;
      render();
    },
    answer(d) { answer(d.id, d.text, 'click'); },
    toggleMulti(d) {
      const list = S.multi[d.id] || [];
      S.multi[d.id] = list.includes(d.text) ? list.filter((x) => x !== d.text) : list.concat(d.text);
      render();
    },
    submitMulti(d) { answer(d.id, (S.multi[d.id] || []).join(', '), 'click'); },
    send(d) { answer(d.id, (S.drafts[d.id] || '').trim(), 'input'); },
    dismiss(d) { post('/api/dismiss', { id: d.id }).then(refresh).catch(() => toast(t('sendFailed'), 'bad')); },
    lang(d) { S.lang = d.v; applyTheme(); render(); saveSettings({ lang: d.v }); },
    theme() { S.theme = resolvedTheme() === 'dark' ? 'light' : 'dark'; applyTheme(); render(); saveSettings({ theme: S.theme }); },
    setTheme(d) { S.theme = d.v; applyTheme(); render(); saveSettings({ theme: d.v }); },
    setDays(d) { saveSettings({ historyDays: Number(d.v) }); },
    setHold(d) { saveSettings({ holdMinutes: Number(d.v) }); },
    settings() { S.settingsOpen = true; S.integrationError = null; render(); },
    closeSettings() { S.settingsOpen = false; render(); },
    integration(d) {
      post('/api/integration', { enable: d.v === 'on' })
        .then((r) => { S.integrationError = r && r.error ? r.error : null; refresh(); })
        .catch(() => toast(t('sendFailed'), 'bad'));
    },
    range(d) { S.range = d.v; render(); },
    filter(d) { S.filter = d.v; render(); },
    reveal(d) { post('/api/reveal', { key: d.id }).catch(() => {}); },
  };

  const changes = {
    toggleTrailing(el) { saveSettings({ holdTrailingQuestions: el.checked }); },
    togglePermission(el) { saveSettings({ relayPermissions: el.checked }); },
  };

  document.addEventListener('click', (e) => {
    const el = e.target.closest('[data-act]');
    if (el && actions[el.dataset.act]) actions[el.dataset.act](el.dataset, el);
  });
  document.addEventListener('change', (e) => {
    const el = e.target.closest('[data-change]');
    if (el && changes[el.dataset.change]) changes[el.dataset.change](el);
  });
  document.addEventListener('input', (e) => {
    const el = e.target.closest('[data-input]');
    if (!el) return;
    if (el.dataset.input === 'draft') S.drafts[el.dataset.id] = el.value;
    else if (el.dataset.input === 'search') { S.search = el.value; render(); }
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && S.zoom) { actions.unzoom(); return; }
    if (e.key === 'Escape' && S.settingsOpen) { actions.closeSettings(); return; }
    if (e.key !== 'Enter' || e.isComposing) return;         // Enter while composing Chinese or Korean is not "send"
    const el = e.target;
    if (el.matches && el.matches('[data-enter]')) actions[el.dataset.enter](el.dataset, el);
    else if (el.matches && el.matches('[role="button"][data-act]')) actions[el.dataset.act](el.dataset, el);
  });

  mq.addEventListener('change', () => { applyTheme(); render(); });
  window.chrome?.webview?.addEventListener('message', (e) => {
    if (e.data && e.data.view) { S.view = e.data.view; kick(); }
  });

  // The window can be opened straight onto a view, e.g. …/#decisions.
  if (['overview', 'decisions', 'timeline', 'session'].includes(location.hash.slice(1))) S.view = location.hash.slice(1);

  applyTheme();
  render();
  refresh();
  const events = new EventSource('/api/events');
  events.onmessage = () => refresh();
  setInterval(() => { if (S.payload) render(); }, 1000);     // clocks and elapsed times
  setInterval(refresh, 15000);                                // safety net if the event stream stalls
})();
