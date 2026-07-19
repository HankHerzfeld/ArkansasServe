
  Auth.init();
  let profile = null;
  let allEvents = [];
  let pendingEventId = null;
  let pendingOrgId   = null;

  Auth.requireAuth().then(async (p) => {
    profile = p;
    if (!profile) return;
    UI.setupHeader('/events.html');
    // The filter list is the EFFECTIVE vocabulary (canonical + approved-new, #10②), fetched so
    // it can never offer a category events can't be created with — and never a pending one.
    // Loaded before rendering so the rowFilter/badges can resolve aliases.
    await Categories.load().catch(() => {});
    Categories.fillSelect(document.getElementById('filter-category'), '', 'All categories');
    loadEvents();
  });

  async function loadEvents() {
    try {
      allEvents = await Api.Events.list();
      document.getElementById('events-loading').style.display = 'none';
      buildSearchIndex(allEvents);
      renderEvents(allEvents);
    } catch (err) {
      document.getElementById('events-loading').innerHTML =
        `<div class="alert alert-error">Could not load events. Please refresh.</div>`;
    }
  }

  // ── Search index ───────────────────────────────────────────────────────────
  // Events stay cards; DataTables just does the searching. It indexes a hidden mirror of
  // the events (#events-index) and the grid renders whatever survives the filter, so the
  // card layout is untouched while the search gets DataTables' behaviour — notably its
  // "smart" search, which matches all whitespace-separated terms in any order and across
  // any column. The old filter was a single substring test over title/org, so
  // "tutoring little rock" found nothing unless it appeared verbatim in one field.
  //
  // Row order mirrors allEvents, so a row index maps straight back to an event.
  // Spots left comes from Availability, which honours per-shift capacity when the event
  // has shifts (see availability.js) and treats an uncapped event as having room.

  // Local calendar day for an event, as yyyy-mm-dd, to compare against the date inputs.
  // Deliberately NOT toISOString(), which converts to UTC and would drift an evening
  // event onto the following day for anyone behind UTC — i.e. everyone in Arkansas.
  function localDayKey(value) {
    const d = new Date(value);
    if (isNaN(d)) return '';
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
  }

  function buildSearchIndex(events) {
    DT.destroy('events-index');
    const body = document.getElementById('events-index-body');
    body.innerHTML = '';

    events.forEach(e => {
      const tr = document.createElement('tr');
      const cells = [
        e.title,
        e.organizationName,
        e.category,
        e.location,
        (e.tags || []).join(' '),
        // Sortable, not displayed. An ISO timestamp sorts lexicographically in
        // chronological order, so no custom comparator is needed.
        e.startDateTime ? new Date(e.startDateTime).toISOString() : '',
        // Uncapped events sort as the roomiest rather than as zero.
        Availability.sortKey(e),
      ];
      cells.forEach(v => {
        const td = document.createElement('td');
        td.textContent = v || '';
        tr.appendChild(td);
      });
      body.appendChild(tr);
    });

    DT.mount('events-index', {
      // Exact and range tests live here rather than in the text search, which can only
      // ask "does this string appear somewhere in the row".
      rowFilter: (dataIndex) => {
        const evt = allEvents[dataIndex];
        if (!evt) return true;

        // Resolve the event's stored value (an alias -> its canonical) before matching, so an
        // aliased category filters under the canonical the dropdown offers. Pending -> null -> excluded.
        const category = document.getElementById('filter-category').value;
        if (category && Categories.resolve(evt.category) !== category) return false;

        const tag = document.getElementById('filter-tag').value;
        if (tag && !(evt.tags || []).includes(tag)) return false;

        // ZIP / town / county (#16). Structured fields, so these only match events created
        // (or edited) since #16 shipped; older events fall to the free-text search over
        // `location`. ZIP is a prefix match; town/county are case-insensitive substrings.
        const zip = document.getElementById('filter-zip').value.replace(/\D/g, '').slice(0, 5);
        if (zip && !(evt.zip || '').startsWith(zip)) return false;

        const town = document.getElementById('filter-town').value.trim().toLowerCase();
        if (town && !(evt.city || '').toLowerCase().includes(town)) return false;

        const county = document.getElementById('filter-county').value.trim().toLowerCase();
        if (county && !(evt.county || '').toLowerCase().includes(county)) return false;

        if (document.getElementById('filter-open-only').checked && !Availability.hasRoom(evt)) return false;

        // Inclusive on both ends: "From 1 Mar / To 1 Mar" means events ON 1 March.
        const day = localDayKey(evt.startDateTime);
        const from = document.getElementById('filter-date-from').value;
        const to = document.getElementById('filter-date-to').value;
        if (from && day && day < from) return false;
        if (to && day && day > to) return false;

        return true;
      },
      dataTables: {
        // Engine only: nothing of DataTables is rendered on this page. Ordering is on
        // (the grid reads rows in the applied order) but there are no headers to click —
        // the sort dropdown drives it.
        paging: false,
        ordering: true,
        scrollX: false,
        info: false,
        order: [[5, 'asc']], // soonest first
        columnDefs: [{ targets: [5, 6], searchable: false }],
        layout: { topStart: null, topEnd: null, bottomStart: null, bottomEnd: null },
      },
    });

    const dt = DT.instance('events-index');
    if (dt) dt.on('draw', renderMatchingEvents);
    populateTagFilter(events);
    applySort();
  }

  // Tags come from the events actually loaded — a hardcoded list would go stale the
  // moment an organizer invents one. Preserves the current choice across reloads.
  function populateTagFilter(events) {
    const sel = document.getElementById('filter-tag');
    const previous = sel.value;
    const tags = [...new Set(events.flatMap(e => e.tags || []).filter(Boolean))]
      .sort((a, b) => a.localeCompare(b));
    sel.innerHTML = '';
    const all = document.createElement('option');
    all.value = ''; all.textContent = 'All tags';
    sel.appendChild(all);
    tags.forEach(t => {
      const o = document.createElement('option');
      o.value = t; o.textContent = t;
      sel.appendChild(o);
    });
    // A tag that no longer exists silently falls back to "All tags".
    sel.value = tags.includes(previous) ? previous : '';
    // Hide the control entirely when no event carries a tag — an "All tags" dropdown
    // with nothing in it is just a dead end.
    sel.style.display = tags.length ? '' : 'none';
  }

  const SORTS = {
    'date-asc':   [5, 'asc'],
    'date-desc':  [5, 'desc'],
    'name-asc':   [0, 'asc'],
    'name-desc':  [0, 'desc'],
    'spots-desc': [6, 'desc'],
  };

  function applySort() {
    const dt = DT.instance('events-index');
    if (!dt) return;
    const [col, dir] = SORTS[document.getElementById('filter-sort').value] || SORTS['date-asc'];
    dt.order([col, dir]).draw();
  }

  // Re-render the grid from whatever the index currently matches.
  //
  // The selector's `order` defaults to 'current', so these indexes come back in the
  // ORDER DataTables has applied — which is what makes the sort dropdown work on cards.
  function renderMatchingEvents() {
    const dt = DT.instance('events-index');
    if (!dt) return renderEvents(allEvents);
    const matched = dt.rows({ search: 'applied' }).indexes().toArray()
      .map(i => allEvents[i])
      .filter(Boolean);
    renderEvents(matched);
    updateFilterCount(matched.length);
  }

  function updateFilterCount(shown) {
    const el = document.getElementById('filter-count');
    if (!el) return;
    const total = allEvents.length;
    el.textContent = shown === total ? `${total} event${total === 1 ? '' : 's'}` : `${shown} of ${total}`;
  }

  function renderEvents(events) {
    const grid = document.getElementById('events-grid');
    const empty = document.getElementById('events-empty');
    if (events.length === 0) {
      grid.style.display  = 'none';
      empty.style.display = 'block';
      return;
    }
    empty.style.display = 'none';
    grid.style.display  = 'grid';

    grid.innerHTML = '';
    events.forEach(evt => {
      const card = document.createElement('div');
      card.className = 'card event-card';

      if (evt.photoUrl) {
        const img = document.createElement('img');
        img.src = evt.photoUrl;
        img.alt = evt.title;
        card.appendChild(img);
      }

      const badge = document.createElement('span');
      badge.className = 'event-badge';
      // Public card: show the resolved canonical (aliases fold in); a pending label stays hidden
      // behind the neutral "Volunteer" until it is approved.
      badge.textContent = Categories.resolve(evt.category) || 'Volunteer';
      card.appendChild(badge);

      const title = document.createElement('a');
      title.className = 'card-title';
      title.href = `/event.html?id=${encodeURIComponent(evt.id)}&organizationId=${encodeURIComponent(evt.organizationId)}`;
      title.textContent = evt.title;
      title.style.cssText = 'display:block;color:var(--green);text-decoration:none;';
      card.appendChild(title);

      const meta = document.createElement('div');
      meta.className = 'card-meta';
      const spotsLabel = Availability.label(evt);
      meta.textContent = `📍 ${evt.location} · 📅 ${new Date(evt.startDateTime).toLocaleString([], { dateStyle:'medium', timeStyle:'short' })} · ⏱ ${evt.hoursValue} hour${evt.hoursValue !== 1 ? 's' : ''} credit${spotsLabel ? ` · ${spotsLabel}` : ''}`;
      card.appendChild(meta);

      const orgName = document.createElement('p');
      orgName.style.cssText = 'font-size:.85rem;color:var(--gray-600);margin-bottom:.75rem;';
      orgName.textContent = evt.organizationName;
      card.appendChild(orgName);

      const status = document.createElement('span');
      status.className = `status status-${evt.status.toLowerCase()}`;
      status.style.cssText = 'margin-bottom:.75rem;display:inline-block;';
      status.textContent = evt.status;
      card.appendChild(status);
      card.appendChild(document.createElement('br'));

      if (profile.adminLevel === 'Student' && evt.status === 'Open') {
        const structured = (evt.shifts && evt.shifts.length) || (evt.signupQuestions && evt.signupQuestions.length);
        if (structured) {
          // Shifts/questions are collected on the detail page.
          const link = document.createElement('a');
          link.className = 'btn btn-primary btn-sm';
          link.href = `/event.html?id=${encodeURIComponent(evt.id)}&organizationId=${encodeURIComponent(evt.organizationId)}`;
          link.textContent = 'Sign Up';
          card.appendChild(link);
        } else {
          const btn = document.createElement('button');
          btn.className = 'btn btn-primary btn-sm';
          btn.textContent = 'Sign Up';
          btn.addEventListener('click', () => openSignup(evt.id, evt.organizationId, evt.title, evt.location));
          card.appendChild(btn);
        }
      }

      grid.appendChild(card);
    });
  }

  // Filters — every control drives the DataTables index, which redraws the grid.
  // Free text goes to DataTables' search; the rest are read by the rowFilter on draw,
  // so they only need to trigger one.
  ['filter-search', 'filter-category', 'filter-tag',
   'filter-zip', 'filter-town', 'filter-county',
   'filter-date-from', 'filter-date-to', 'filter-open-only'].forEach(id => {
    const el = document.getElementById(id);
    // 'input' doesn't fire for a checkbox in every browser; 'change' covers both, and
    // covers date pickers where 'input' can fire mid-typing on a half-entered date.
    el?.addEventListener('input', applyEventFilters);
    el?.addEventListener('change', applyEventFilters);
  });

  document.getElementById('filter-sort')?.addEventListener('change', applySort);

  document.getElementById('filter-clear')?.addEventListener('click', () => {
    document.getElementById('filter-search').value = '';
    document.getElementById('filter-category').value = '';
    document.getElementById('filter-tag').value = '';
    document.getElementById('filter-zip').value = '';
    document.getElementById('filter-town').value = '';
    document.getElementById('filter-county').value = '';
    document.getElementById('filter-date-from').value = '';
    document.getElementById('filter-date-to').value = '';
    document.getElementById('filter-open-only').checked = false;
    document.getElementById('filter-sort').value = 'date-asc';

    // Clearing the input's value is not enough — DataTables holds the search term
    // itself, so it must be reset explicitly or Clear would leave the text filter on.
    // Search + order are set together so this costs one redraw, not two.
    const dt = DT.instance('events-index');
    if (!dt) return;
    const [col, dir] = SORTS['date-asc'];
    dt.search('').order([col, dir]).draw();
  });

  function applyEventFilters() {
    const dt = DT.instance('events-index');
    if (!dt) return;
    dt.search(document.getElementById('filter-search').value.trim()).draw();
  }

  // Sign-up modal
  function openSignup(eventId, orgId, title, location) {
    pendingEventId = eventId;
    pendingOrgId   = orgId;
    document.getElementById('modal-event-title').textContent   = `Sign Up: ${title}`;
    document.getElementById('modal-event-details').textContent = `📍 ${location}`;
    document.getElementById('signup-modal').classList.add('open');
  }

  document.getElementById('modal-cancel').addEventListener('click', () => {
    document.getElementById('signup-modal').classList.remove('open');
  });

  document.getElementById('modal-confirm').addEventListener('click', async () => {
    const btn = document.getElementById('modal-confirm');
    btn.disabled = true;
    btn.textContent = 'Signing up…';
    try {
      await Api.Registrations.create({ eventId: pendingEventId, organizationId: pendingOrgId });
      document.getElementById('signup-modal').classList.remove('open');
      await loadEvents(); // refresh to update slot counts
      showToast('You\'re signed up! We\'ll see you there.', 'success');
    } catch (err) {
      showToast(err.message || 'Sign-up failed. Please try again.', 'error');
    } finally {
      btn.disabled = false;
      btn.textContent = 'Confirm Sign-Up';
    }
  });

  function showToast(message, type = 'success') {
    const toast = document.createElement('div');
    toast.className = `alert alert-${type}`;
    // bottom/right are safe-area aware: the page is edge-to-edge, so a flat 1.5rem would
    // put the toast under the home indicator (portrait) or the notch (landscape).
    toast.style.cssText = 'position:fixed;bottom:max(1.5rem,env(safe-area-inset-bottom));right:max(1.5rem,env(safe-area-inset-right));z-index:999;max-width:320px;box-shadow:0 4px 12px rgba(0,0,0,.15);';
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
  }
