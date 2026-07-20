// maps.js — loads the Google Maps JS API on demand, and geocodes addresses.
//
// The key comes from GET /api/config/maps (a Function App setting), never hardcoded — this
// repository is PUBLIC, and serving it from a setting also means it can be rotated without a
// code change. The key is public by nature once it reaches the browser; its safety comes from
// being HTTP-referrer restricted to arkansasserve.com and API-restricted to the three APIs.
//
// DEGRADES QUIETLY. Every entry point resolves to null rather than throwing when the key is
// unset or the script fails to load, so an event form still works via the bundled Arkansas ZIP
// dataset (#16). Maps are an enhancement over that, not a replacement for it — a missing app
// setting must never make it impossible to create an event.
//
// REQUIRES api.js.

'use strict';

const Maps = (() => {
  // The script tag is global, so loading is memoised: many callers, one <script>, one load.
  let loadPromise = null;
  let cachedConfig = null;

  async function config() {
    if (cachedConfig) return cachedConfig;
    try {
      cachedConfig = await Api.Config.maps();
    } catch {
      // Endpoint missing (not yet deployed) or the call failed — treat as "no maps".
      cachedConfig = { enabled: false, apiKey: null };
    }
    return cachedConfig;
  }

  // Resolves to the `google.maps` namespace, or null when maps are unavailable.
  function load() {
    if (loadPromise) return loadPromise;

    loadPromise = (async () => {
      const cfg = await config();
      if (!cfg.enabled || !cfg.apiKey) return null;
      if (window.google?.maps) return window.google.maps;

      return new Promise((resolve) => {
        const script = document.createElement('script');
        // `loading=async` is what Google asks for and silences its console warning.
        script.src = 'https://maps.googleapis.com/maps/api/js'
          + `?key=${encodeURIComponent(cfg.apiKey)}&libraries=places&loading=async&v=weekly`;
        script.async = true;

        // ⚠️ onload IS NOT READY. With `loading=async` the script that arrives is only the
        // BOOTSTRAP: `window.google.maps` exists immediately, but `google.maps.Map` and
        // `google.maps.places` are still being fetched. Resolving on onload therefore hands
        // callers a namespace whose constructors are undefined — "maps.Map is not a
        // constructor", and `places` undefined for Autocomplete.
        //
        // This failed ASYMMETRICALLY and that is why it survived a first pass: `Geocoder` is
        // part of the core bundle and was usually ready, so geocoding worked while the map and
        // autocomplete threw. Await the libraries explicitly.
        script.onload = async () => {
          try {
            const g = window.google?.maps;
            if (!g?.importLibrary) { resolve(g || null); return; }   // older bootstrap: already whole
            await Promise.all([g.importLibrary('maps'), g.importLibrary('places')]);
            resolve(window.google.maps);
          } catch (err) {
            console.warn('[maps] Google Maps libraries failed to load', err);
            resolve(null);
          }
        };
        // Resolve null rather than reject: a blocked or failed script must degrade, not throw
        // into every caller. The most likely cause is a CSP or referrer-restriction mismatch.
        script.onerror = () => { console.warn('[maps] Google Maps failed to load'); resolve(null); };
        document.head.appendChild(script);
      });
    })();

    return loadPromise;
  }

  async function available() { return (await load()) != null; }

  // Address string -> { lat, lng, zip, city, county, formatted } or null.
  //
  // Runs CLIENT-SIDE deliberately. The Function App is Consumption (Y1) with no stable outbound
  // IPs, so a server-side key could not be IP-restricted and would be strictly weaker than the
  // referrer-restricted browser key. The backend never calls Google; it only stores what the
  // client resolved.
  async function geocode(address) {
    const maps = await load();
    if (!maps || !address) return null;

    const geocoder = new maps.Geocoder();
    const { results } = await geocoder.geocode({
      address,
      // Arkansas-only programme: bias hard so "Springfield" resolves in-state.
      componentRestrictions: { country: 'US', administrativeArea: 'AR' },
    }).catch(() => ({ results: [] }));

    const hit = results?.[0];
    if (!hit) return null;
    return { ...parseComponents(hit.address_components || []), ...coordsOf(hit), formatted: hit.formatted_address };
  }

  function coordsOf(result) {
    const loc = result.geometry?.location;
    if (!loc) return { lat: null, lng: null };
    // lat()/lng() are functions on a LatLng but plain numbers once serialised — handle both.
    return {
      lat: typeof loc.lat === 'function' ? loc.lat() : loc.lat,
      lng: typeof loc.lng === 'function' ? loc.lng() : loc.lng,
    };
  }

  // Google returns an unordered component list; pull out the pieces the Event model stores.
  // `county` arrives as "Pulaski County" — the stored form throughout this app is the bare
  // name ("Pulaski"), matching the bundled ZIP dataset, so the suffix is stripped.
  function parseComponents(components) {
    const find = (type) => components.find(c => (c.types || []).includes(type));
    const county = find('administrative_area_level_2')?.long_name || '';
    return {
      zip:    find('postal_code')?.long_name || '',
      city:   find('locality')?.long_name || find('sublocality')?.long_name || '',
      county: county.replace(/\s+County$/i, ''),
    };
  }

  // Attaches Places Autocomplete to a text input. Calls onPlace({lat,lng,zip,city,county,formatted})
  // when a suggestion is chosen. No-op (returns false) when maps are unavailable, so the caller
  // keeps whatever manual behaviour it already had.
  async function attachAutocomplete(input, onPlace) {
    const maps = await load();
    // Belt and braces: load() now awaits the places library, but a caller must never get an
    // EXCEPTION out of an enhancement. Missing constructor -> return false and let the form keep
    // manual entry, exactly as when there is no key at all. This threw before, and because
    // org-portal called it with .then() and no .catch it surfaced as an unhandled rejection.
    if (!maps || !input || !maps.places?.Autocomplete) return false;

    const ac = new maps.places.Autocomplete(input, {
      componentRestrictions: { country: 'us' },
      fields: ['address_components', 'geometry', 'formatted_address'],
    });
    ac.addListener('place_changed', () => {
      const place = ac.getPlace();
      if (!place?.geometry) return;   // free text with no suggestion picked
      onPlace({
        ...parseComponents(place.address_components || []),
        ...coordsOf(place),
        formatted: place.formatted_address,
      });
    });
    return true;
  }

  return { load, available, geocode, attachAutocomplete };
})();
