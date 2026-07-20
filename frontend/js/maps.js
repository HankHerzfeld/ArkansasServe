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
        // ⚠️ DO NOT RESOLVE ON script.onload. Two separate failures came from trying:
        //
        //   1. With `loading=async` the file that arrives is only the BOOTSTRAP. On a COLD
        //      load, `google.maps.importLibrary` does not exist yet at onload — so a guard
        //      like `if (!importLibrary) resolve(namespace)` hands back an EMPTY namespace and
        //      callers get "maps.Map is not a constructor".
        //   2. Even once importLibrary exists, `google.maps.Map` and `.places` do not until
        //      the libraries are imported.
        //
        // It also failed ASYMMETRICALLY, which is how it survived a spot check: `Geocoder`
        // resolves earlier than the rest, so geocoding worked while the map and autocomplete
        // threw — one passing path is not a working feature.
        //
        // `callback=` is the only signal Google guarantees fires when the API is genuinely
        // ready; importLibrary is safe to use from inside it.
        const CB = '__arkansasServeMapsReady';
        window[CB] = async () => {
          try {
            const g = window.google.maps;
            // Import everything this app constructs, rather than trusting which pieces the
            // core bundle happens to attach: Map/InfoWindow, Marker, Autocomplete, Geocoder.
            await Promise.all([
              g.importLibrary('maps'),
              g.importLibrary('marker'),
              g.importLibrary('places'),
              g.importLibrary('geocoding'),
            ]);
            resolve(window.google.maps);
          } catch (err) {
            console.warn('[maps] Google Maps libraries failed to load', err);
            resolve(null);
          } finally {
            delete window[CB];
          }
        };

        const script = document.createElement('script');
        script.src = 'https://maps.googleapis.com/maps/api/js'
          + `?key=${encodeURIComponent(cfg.apiKey)}&libraries=places,marker,geocoding`
          + `&loading=async&v=weekly&callback=${CB}`;
        script.async = true;
        // Resolve null rather than reject: a blocked or failed script must degrade, not throw
        // into every caller. The most likely cause is a CSP or referrer-restriction mismatch.
        script.onerror = () => {
          console.warn('[maps] Google Maps failed to load');
          delete window[CB];
          resolve(null);
        };
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
