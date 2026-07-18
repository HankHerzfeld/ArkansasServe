// categories.js — the effective service-category vocabulary for the UI (#10②).
//
// The canonical list is still code-defined (Taxonomy.SERVICE_CATEGORIES), but approved-new
// values live server-side, so dropdowns and facets must render the EFFECTIVE list fetched from
// /api/categories rather than the hardcoded one. This module fetches it once (cached), resolves
// a stored value for display (alias -> canonical; a not-yet-approved label -> "Other (pending
// review)"), and helps forms run the "Other -> propose a new label" flow.
//
// Distinct from Api.Categories (the raw endpoint): this is the UI helper layered on top.
'use strict';

const Categories = (() => {
  const OTHER = 'Other';
  let _cache = null;        // { effective:[], canonical:[], aliases:{} }
  let _inflight = null;

  async function load(force) {
    if (force) { _cache = null; _inflight = null; }
    if (_cache) return _cache;
    if (!_inflight) {
      _inflight = Api.Categories.list()
        .then(r => { _cache = { effective: r.effective || [], canonical: r.canonical || [], aliases: r.aliases || {} }; return _cache; })
        .catch(err => { _inflight = null; throw err; })
        .finally(() => { _inflight = null; });
    }
    return _inflight;
  }

  function refresh() { return load(true); }

  const norm = v => String(v || '').trim().toLowerCase();
  function findCi(list, v) { const t = norm(v); return (list || []).find(x => norm(x) === t) || null; }
  function aliasOf(v) {
    const a = _cache?.aliases || {};
    const t = norm(v);
    for (const k of Object.keys(a)) if (norm(k) === t) return a[k];
    return null;
  }

  // The canonical value a stored category resolves to (for filtering), or null when it is empty,
  // pending, or unknown — so a pending value never matches a real facet.
  function resolve(v) {
    if (!v) return null;
    return findCi(_cache?.effective, v) || aliasOf(v) || null;
  }

  // A value that is neither a known category nor an alias — its owner sees it as pending.
  function isPending(v) { return !!v && !findCi(_cache?.effective, v) && !aliasOf(v); }

  // Label for a badge shown to the value's OWNER (forms/settings): canonical, or pending marker.
  function displayLabel(v) {
    if (!v) return '';
    return resolve(v) || `${OTHER} (pending review)`;
  }

  // Fill a <select> from the effective list. When the stored value is pending it matches no
  // option, so "Other" is selected instead (the propose input then carries the pending label).
  async function fillSelect(sel, selected, placeholder) {
    if (!sel) return;
    await load();
    sel.innerHTML = '';
    if (placeholder != null) {
      const o = document.createElement('option'); o.value = ''; o.textContent = placeholder; sel.appendChild(o);
    }
    _cache.effective.forEach(v => {
      const o = document.createElement('option'); o.value = v; o.textContent = v;
      if (norm(selected) === norm(v)) o.selected = true;
      sel.appendChild(o);
    });
    if (selected && isPending(selected)) {
      const other = [...sel.options].find(o => norm(o.value) === norm(OTHER));
      if (other) other.selected = true;
    }
  }

  // Wire an "Other -> type a label" input to a select: the input (and optional hint) show only
  // when Other is chosen. Pass { pendingValue } to pre-fill a loaded pending label.
  function wirePropose(sel, input, opts = {}) {
    if (!sel || !input) return;
    const hint = opts.hintEl || null;
    function sync() {
      const isOther = norm(sel.value) === norm(OTHER);
      input.style.display = isOther ? '' : 'none';
      if (hint) hint.style.display = (isOther && input.value.trim()) ? '' : 'none';
    }
    sel.addEventListener('change', sync);
    input.addEventListener('input', sync);
    if (opts.pendingValue && isPending(opts.pendingValue)) input.value = opts.pendingValue;
    sync();
  }

  // The value a form submits: the typed label when "Other" + text is entered, else the select.
  function valueFrom(sel, input) {
    if (sel && norm(sel.value) === norm(OTHER)) {
      const typed = (input?.value || '').trim();
      if (typed) return typed;
    }
    return sel ? sel.value : '';
  }

  // The cached effective list (call after load()). Empty until loaded.
  function list() { return _cache?.effective || []; }

  return { load, refresh, list, resolve, isPending, displayLabel, fillSelect, wirePropose, valueFrom, OTHER };
})();
