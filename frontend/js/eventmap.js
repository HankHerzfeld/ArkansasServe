// eventmap.js — the events map (#18).
//
// Draws whatever the events page has already filtered. It never filters anything itself: the
// map is a second RENDERING of PR #70's result set, exactly as the card grid is, so there is one
// filter state and it can never drift between the two views.
//
// ⚠️ COORDINATE PRECISION IS MIXED, BY DESIGN. Events created before #17 carry a ZIP CENTROID
// from the bundled dataset (#16); events geocoded since carry a real street position. We cannot
// tell them apart from the value, so:
//   - every pin says its position may be approximate, and
//   - events sharing an identical coordinate (the centroid case — every event in one ZIP lands
//     on the same point) are fanned out, or they would hide one another completely.
// Proximity clustering at low zoom is deliberately NOT implemented: it needs a library, and the
// real problem here is exact coincidence, not crowding.
//
// REQUIRES maps.js. Degrades to "no map" when Maps is unconfigured.

'use strict';

const EventMap = (() => {
  // Roughly the centre of Arkansas — the view before anything is plotted.
  const AR_CENTER = { lat: 34.80, lng: -92.20 };
  const AR_ZOOM = 7;

  let map = null;
  let infoWindow = null;
  let markers = new Map();   // eventId -> google.maps.Marker
  let onSelect = null;

  // Mounts the map into `container`. Returns false when Maps is unavailable, so the caller can
  // hide the map UI entirely rather than showing an empty grey box.
  async function mount(container, { onEventSelect } = {}) {
    if (!container) return false;
    const maps = await Maps.load();
    if (!maps) return false;

    onSelect = onEventSelect || null;
    map = new maps.Map(container, {
      center: AR_CENTER,
      zoom: AR_ZOOM,
      mapTypeControl: false,
      streetViewControl: false,
      fullscreenControl: false,
      // Keeps the map from swallowing page scroll on a phone — two-finger pan is the
      // convention and prevents trapping the reader mid-page.
      gestureHandling: 'cooperative',
    });
    infoWindow = new maps.InfoWindow();
    return true;
  }

  const isMounted = () => map != null;

  // Events sharing an EXACT coordinate are spread around a small circle so each is clickable.
  // Deterministic (index-based, not random) so a marker does not jump between redraws.
  function fanOut(events) {
    const groups = new Map();
    events.forEach(e => {
      const key = `${e.latitude},${e.longitude}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(e);
    });

    const placed = [];
    groups.forEach(group => {
      if (group.length === 1) {
        placed.push({ evt: group[0], lat: group[0].latitude, lng: group[0].longitude, fanned: false });
        return;
      }
      // ~35m radius: visibly separate at street zoom, still clearly the same place at city zoom.
      const radius = 0.00035;
      group.forEach((evt, i) => {
        const angle = (2 * Math.PI * i) / group.length;
        placed.push({
          evt,
          lat: evt.latitude + radius * Math.cos(angle),
          lng: evt.longitude + radius * Math.sin(angle),
          fanned: true,
        });
      });
    });
    return placed;
  }

  // Replaces every pin with the given events. Returns { plotted, missing } so the page can be
  // honest about events it could not place rather than quietly dropping them.
  async function render(events) {
    if (!isMounted()) return { plotted: 0, missing: 0 };
    const maps = await Maps.load();

    markers.forEach(m => m.setMap(null));
    markers.clear();
    infoWindow?.close();

    const mappable = (events || []).filter(e => e.latitude != null && e.longitude != null);
    const missing = (events || []).length - mappable.length;

    const bounds = new maps.LatLngBounds();
    fanOut(mappable).forEach(({ evt, lat, lng, fanned }) => {
      const marker = new maps.Marker({
        position: { lat, lng },
        map,
        title: evt.title,
      });
      marker.addListener('click', () => {
        infoWindow.setContent(infoContent(evt, fanned));
        infoWindow.open({ anchor: marker, map });
        if (onSelect) onSelect(evt.id);
      });
      markers.set(evt.id, marker);
      bounds.extend({ lat, lng });
    });

    if (mappable.length === 1) {
      map.setCenter(bounds.getCenter());
      map.setZoom(13);            // fitBounds on a single point zooms to the maximum
    } else if (mappable.length > 1) {
      map.fitBounds(bounds, 48);
    } else {
      map.setCenter(AR_CENTER);
      map.setZoom(AR_ZOOM);
    }

    return { plotted: mappable.length, missing };
  }

  // Built as DOM, not an HTML string, so event text can never be interpreted as markup.
  function infoContent(evt, fanned) {
    const wrap = document.createElement('div');
    wrap.style.cssText = 'font-size:.85rem;max-width:15rem;';

    const link = document.createElement('a');
    link.href = `/event.html?id=${encodeURIComponent(evt.id)}&organizationId=${encodeURIComponent(evt.organizationId)}`;
    link.textContent = evt.title;
    link.style.cssText = 'font-weight:600;color:var(--green);text-decoration:none;display:block;margin-bottom:.2rem;';
    wrap.appendChild(link);

    const when = document.createElement('div');
    when.textContent = new Date(evt.startDateTime).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
    when.style.color = 'var(--gray-600)';
    wrap.appendChild(when);

    if (evt.location) {
      const where = document.createElement('div');
      where.textContent = evt.location;
      where.style.color = 'var(--gray-600)';
      wrap.appendChild(where);
    }

    const note = document.createElement('div');
    note.textContent = fanned
      ? 'Position approximate — several events share this location.'
      : 'Position may be approximate.';
    note.style.cssText = 'margin-top:.35rem;font-size:.75rem;color:var(--gray-600);font-style:italic;';
    wrap.appendChild(note);

    return wrap;
  }

  // Card hover -> raise the matching pin. Google's DROP animation is deliberately not used;
  // it re-drops the pin from off-screen on every hover, which reads as the map glitching.
  function highlight(eventId) {
    markers.forEach((m, id) => m.setZIndex(id === eventId ? 999 : 1));
    const m = markers.get(eventId);
    if (!m) return;
    // BOUNCE runs until cleared, so it is a genuine sustained animation — honour the same
    // prefers-reduced-motion contract main.css already keeps. Raising the z-index above
    // still distinguishes the pin without moving anything.
    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    if (!reduced) m.setAnimation(window.google?.maps?.Animation?.BOUNCE ?? null);
  }

  function clearHighlight() {
    markers.forEach(m => m.setAnimation(null));
  }

  // Marker click -> scroll the card into view and flash it.
  function focusEvent(eventId) {
    const marker = markers.get(eventId);
    if (!marker || !map) return;
    map.panTo(marker.getPosition());
    window.google?.maps?.event?.trigger(marker, 'click');
  }

  return { mount, isMounted, render, highlight, clearHighlight, focusEvent };
})();
