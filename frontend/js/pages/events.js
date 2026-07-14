
  Auth.init();
  let profile = null;
  let allEvents = [];
  let pendingEventId = null;
  let pendingOrgId   = null;

  Auth.requireAuth().then((p) => {
    profile = p;
    if (!profile) return;
    UI.setupHeader('/events.html');
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
  function buildSearchIndex(events) {
    DT.destroy('events-index');
    const body = document.getElementById('events-index-body');
    body.innerHTML = '';

    events.forEach(e => {
      const tr = document.createElement('tr');
      [e.title, e.organizationName, e.category, e.location].forEach(v => {
        const td = document.createElement('td');
        td.textContent = v || '';
        tr.appendChild(td);
      });
      body.appendChild(tr);
    });

    DT.mount('events-index', {
      // Category is a dropdown, not free text — an exact match, so it's checked against
      // the source record rather than folded into the text search.
      rowFilter: (dataIndex) => {
        const evt = allEvents[dataIndex];
        if (!evt) return true;
        const category = document.getElementById('filter-category').value;
        return !category || evt.category === category;
      },
      dataTables: {
        // Engine only: nothing of DataTables is rendered on this page.
        paging: false,
        ordering: false,
        scrollX: false,
        info: false,
        layout: { topStart: null, topEnd: null, bottomStart: null, bottomEnd: null },
      },
    });

    const dt = DT.instance('events-index');
    if (dt) dt.on('draw', renderMatchingEvents);
  }

  // Re-render the grid from whatever the index currently matches.
  function renderMatchingEvents() {
    const dt = DT.instance('events-index');
    if (!dt) return renderEvents(allEvents);
    const matched = dt.rows({ search: 'applied' }).indexes().toArray()
      .map(i => allEvents[i])
      .filter(Boolean);
    renderEvents(matched);
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
      badge.textContent = evt.category || 'Volunteer';
      card.appendChild(badge);

      const title = document.createElement('a');
      title.className = 'card-title';
      title.href = `/event.html?id=${encodeURIComponent(evt.id)}&organizationId=${encodeURIComponent(evt.organizationId)}`;
      title.textContent = evt.title;
      title.style.cssText = 'display:block;color:var(--green);text-decoration:none;';
      card.appendChild(title);

      const meta = document.createElement('div');
      meta.className = 'card-meta';
      meta.textContent = `📍 ${evt.location} · 📅 ${new Date(evt.startDateTime).toLocaleString([], { dateStyle:'medium', timeStyle:'short' })} · ⏱ ${evt.hoursValue} hour${evt.hoursValue !== 1 ? 's' : ''} credit${evt.maxSlots > 0 ? ` · ${evt.maxSlots - evt.currentSlots} spots left` : ''}`;
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

  // Filter — both controls now drive the DataTables index, which redraws the grid.
  ['filter-search', 'filter-category'].forEach(id => {
    document.getElementById(id)?.addEventListener('input', () => {
      const dt = DT.instance('events-index');
      if (!dt) return;
      dt.search(document.getElementById('filter-search').value.trim()).draw();
    });
  });

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
    toast.style.cssText = 'position:fixed;bottom:1.5rem;right:1.5rem;z-index:999;max-width:320px;box-shadow:0 4px 12px rgba(0,0,0,.15);';
    toast.textContent = message;
    document.body.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
  }
