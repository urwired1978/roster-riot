// Roster Riot: the "feel" layer. Loaded once by Layout/MainLayout.razor.
//   1. Page transitions: in-app link taps go through the View Transitions API (Safari 18+, Chrome),
//      with a CSS animation fallback. Tabs crossfade; opening a player/team/matchup pushes in from the
//      right; the Back button pops back and restores where you were scrolled.
//   2. Pull to refresh: touch events (works in the iPhone home-screen app, where the page can't
//      overscroll), with rubber-band resistance and a release threshold. Calls .NET PullRefresh().
//   3. Live numbers ease from the old value to the new one when scores refresh.
//   4. A no-op touchstart listener so iOS applies :active pressed styles.
// Everything respects prefers-reduced-motion.

const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
let dotnet = null;
let started = false;

export function init(dotnetRef) {
  dotnet = dotnetRef;
  if (started) return;
  started = true;
  document.addEventListener('touchstart', () => { }, { passive: true });
  setupNavigation();
  setupPullToRefresh();
  setupNumberEasing();
}

/** Called by .NET right before fresh league data renders, so number changes ease (but filter/tab changes don't). */
export function markLive() { liveUntil = performance.now() + 1500; }

// ───────────────────────── 1. Page transitions ─────────────────────────
const scrollMemory = new Map();
const DETAIL = /^(player|team|matchup)\/./;   // pages that "push" on top of a tab

function relPath(url) { return url.href.startsWith(document.baseURI) ? url.href.slice(document.baseURI.length).split(/[?#]/)[0] : null; }

function setupNavigation() {
  window.addEventListener('click', e => {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    const target = e.target instanceof Element ? e.target : null;
    if (!target) return;

    const back = target.closest('[data-nav="back"]');
    if (back) {
      e.preventDefault(); e.stopPropagation();
      goBack();
      return;
    }

    const a = target.closest('a[href]');
    if (!a || (a.target && a.target !== '_self') || a.hasAttribute('download')) return;
    const url = new URL(a.href, document.baseURI);
    const to = relPath(url);
    if (url.origin !== location.origin || to === null) return;

    e.preventDefault(); e.stopPropagation();   // we drive Blazor's router ourselves
    if (url.href === location.href) {          // re-tapping the current tab: back to the top
      window.scrollTo({ top: 0, behavior: reduceMotion.matches ? 'auto' : 'smooth' });
      return;
    }
    const fromDetail = DETAIL.test(relPath(new URL(location.href)) ?? '');
    const kind = a.closest('.tabbar') ? 'tab' : DETAIL.test(to) ? 'forward' : fromDetail ? 'back' : 'tab';
    navigate(() => window.Blazor.navigateTo(url.href), kind, false);
  }, true);
}

function goBack() {
  // Opened straight onto a player card (no history inside the app): go to the Matchup tab instead.
  if (history.length <= 1) { navigate(() => window.Blazor.navigateTo(document.baseURI), 'back', false); return; }
  navigate(() => history.back(), 'back', true);
}

/** Resolves once Blazor has rendered the new page (first DOM change under #app), or after a timeout. */
function pageRendered(timeout = 450) {
  return new Promise(resolve => {
    const root = document.getElementById('app') || document.body;
    let done = false;
    const finish = () => { if (done) return; done = true; obs.disconnect(); clearTimeout(timer); resolve(); };
    const obs = new MutationObserver(finish);
    obs.observe(root, { childList: true, subtree: true, characterData: true });
    const timer = setTimeout(finish, timeout);
  });
}

let navigating = false;
async function navigate(go, kind, restoreScroll) {
  if (navigating) { go(); return; }
  navigating = true;
  scrollMemory.set(location.href, window.scrollY);
  const update = async () => {
    const rendered = pageRendered();
    go();
    await rendered;
    window.scrollTo(0, restoreScroll ? (scrollMemory.get(location.href) ?? 0) : 0);
  };
  const root = document.documentElement;
  try {
    if (reduceMotion.matches) {
      await update();
    } else if (document.startViewTransition) {
      root.dataset.nav = kind;
      const vt = document.startViewTransition(update);
      await vt.updateCallbackDone.catch(() => { });
      navigating = false;
      await vt.finished.catch(() => { });
      if (!navigating) delete root.dataset.nav;
    } else {
      await update();
      const c = document.querySelector('.content');
      if (c) {
        c.classList.remove('page-in-tab', 'page-in-forward', 'page-in-back');
        void c.offsetWidth;
        c.classList.add('page-in-' + kind);
        c.addEventListener('animationend', () => c.classList.remove('page-in-' + kind), { once: true });
      }
    }
  } finally {
    navigating = false;
  }
}

// ───────────────────────── 2. Pull to refresh ─────────────────────────
const THRESHOLD = 64;   // px of visible pull needed to refresh
const HOLD = 52;        // where the content rests while refreshing
const MAX = 150;        // the pull can never go past this
const SOFT = 210;       // resistance: bigger = less resistance
const MIN_SPIN = 650;   // keep the spinner up at least this long so it doesn't flash
const RING = 2 * Math.PI * 8;

function setupPullToRefresh() {
  const ind = document.createElement('div');
  ind.className = 'ptr';
  ind.setAttribute('aria-hidden', 'true');
  ind.innerHTML = `<svg viewBox="0 0 24 24"><circle class="ptr-track" cx="12" cy="12" r="8"/><circle class="ptr-arc" cx="12" cy="12" r="8" transform="rotate(-90 12 12)" stroke-dasharray="${RING.toFixed(2)}" stroke-dashoffset="${RING.toFixed(2)}"/></svg>`;
  document.body.appendChild(ind);
  const svg = ind.firstElementChild;
  const arc = ind.querySelector('.ptr-arc');

  let startX = 0, startY = 0, decided = true, pulling = false, busy = false, dist = 0;
  const content = () => document.querySelector('.content');

  function paint(d, loading) {
    const c = content();
    if (c) c.style.transform = d > 0.5 ? `translate3d(0, ${d.toFixed(1)}px, 0)` : '';
    const p = Math.min(1, d / THRESHOLD);
    ind.style.opacity = loading ? '1' : String(Math.max(0, Math.min(1, (d - 10) / 30)));
    ind.style.transform = `translateY(${(Math.max(d, 0) + 16) / 2 - 18}px) scale(${0.7 + 0.3 * p})`;
    if (!loading) {
      svg.style.transform = `rotate(${(d * 2.4).toFixed(0)}deg)`;
      arc.setAttribute('stroke-dashoffset', (RING * (1 - p * 0.9)).toFixed(2));
    }
    ind.classList.toggle('armed', !loading && d >= THRESHOLD);
  }

  function settle(d, loading) {
    const c = content();
    c?.classList.add('settle');
    ind.classList.add('settle');
    ind.classList.toggle('loading', loading);
    if (loading) arc.setAttribute('stroke-dashoffset', (RING * 0.72).toFixed(2));
    paint(d, loading);
    if (!loading && d === 0) {
      ind.style.opacity = '0';
      setTimeout(() => { if (!pulling && !busy) { c?.classList.remove('settle'); ind.classList.remove('settle'); if (c) c.style.transform = ''; } }, 300);
    }
  }

  window.addEventListener('touchstart', e => {
    decided = true; pulling = false;
    if (busy || e.touches.length !== 1 || window.scrollY > 0) return;
    const t = e.target instanceof Element ? e.target : null;
    if (!t || t.closest('.tabbar, .topbar, select, input, textarea, [data-no-ptr]') || !content()) return;
    startX = e.touches[0].clientX; startY = e.touches[0].clientY; decided = false; dist = 0;
  }, { passive: true });

  window.addEventListener('touchmove', e => {
    if (decided && !pulling) return;
    const t = e.touches[0];
    if (!decided) {
      const dx = t.clientX - startX, dy = t.clientY - startY;
      if (Math.abs(dx) < 6 && Math.abs(dy) < 6) return;
      decided = true;
      pulling = dy > 0 && Math.abs(dy) > Math.abs(dx) * 1.2 && window.scrollY <= 0;
      if (!pulling) return;
      startY = t.clientY;
      content()?.classList.remove('settle');
      ind.classList.remove('settle', 'loading');
    }
    if (e.cancelable) e.preventDefault();
    const raw = Math.max(0, t.clientY - startY);
    dist = MAX * (1 - Math.exp(-raw / SOFT));
    paint(dist, false);
  }, { passive: false });

  const end = async () => {
    if (!pulling) { decided = true; return; }
    pulling = false; decided = true;
    if (dist < THRESHOLD) { settle(0, false); return; }
    busy = true;
    settle(reduceMotion.matches ? 0 : HOLD, true);
    if (reduceMotion.matches) ind.style.opacity = '1';
    const t0 = performance.now();
    try { await dotnet?.invokeMethodAsync('PullRefresh'); } catch (err) { console.warn('Pull to refresh failed:', err); }
    const wait = MIN_SPIN - (performance.now() - t0);
    if (wait > 0) await new Promise(r => setTimeout(r, wait));
    busy = false;
    ind.classList.remove('loading');
    settle(0, false);
  };
  window.addEventListener('touchend', end, { passive: true });
  window.addEventListener('touchcancel', end, { passive: true });
}

// ───────────────────────── 3. Live number easing ─────────────────────────
// Numbers in these spots count from the old value to the new one when live data changes.
const EASE = '.hero-scores span, .vs-pts, .prow-nums .big, .sc-row b, .mini-row b, .g-score, .pw-num, .ring-num, .gm b, .odds-pct, .st-pf, [data-ease]';
const NUM = /^(\D*?)(-?\d[\d,]*(?:\.\d+)?)(\D*)$/;
const EASE_MS = 450;
let liveUntil = 0;

function parse(text) {
  const m = NUM.exec((text ?? '').trim());
  if (!m) return null;
  const digits = m[2];
  const dot = digits.indexOf('.');
  return { pre: m[1], post: m[3], n: parseFloat(digits.replace(/,/g, '')), dec: dot < 0 ? 0 : digits.length - dot - 1, comma: digits.includes(',') };
}

function format(n, dec, comma) {
  return comma ? n.toLocaleString('en-US', { minimumFractionDigits: dec, maximumFractionDigits: dec }) : n.toFixed(dec);
}

function setupNumberEasing() {
  const running = new WeakMap();
  const root = document.getElementById('app') || document.body;
  const obs = new MutationObserver(records => {
    if (reduceMotion.matches || document.hidden || performance.now() > liveUntil) return;
    for (const r of records) {
      const node = r.target;
      if (node.nodeType !== Node.TEXT_NODE) continue;
      const el = node.parentElement;
      if (!el || !el.matches(EASE)) continue;
      const from = parse(r.oldValue), to = parse(node.nodeValue);
      if (!from || !to || from.pre !== to.pre || from.post !== to.post || from.n === to.n) continue;
      tween(node, from.n, to, node.nodeValue);
    }
  });
  obs.observe(root, { subtree: true, characterData: true, characterDataOldValue: true });

  function tween(node, from, to, finalText) {
    cancelAnimationFrame(running.get(node));
    const t0 = performance.now();
    const step = now => {
      const k = Math.min(1, (now - t0) / EASE_MS);
      const e = 1 - Math.pow(1 - k, 3);
      node.nodeValue = k < 1 ? to.pre + format(from + (to.n - from) * e, to.dec, to.comma) + to.post : finalText;
      obs.takeRecords();   // ignore our own writes
      if (k < 1) running.set(node, requestAnimationFrame(step)); else running.delete(node);
    };
    running.set(node, requestAnimationFrame(step));
  }
}
