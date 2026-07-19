// orgs.js — the two Arkansas Serve organization ids, and how to resolve between them.
//
// There are deliberately TWO "Arkansas Serve" records, and mixing them up is easy:
//
//   arkansas-serve-root  the INTERNAL PLATFORM PARTITION. Every @arkansasserve.com account
//                        auto-lands here, both demo SuperAdmins live here, and it is filtered
//                        out of the public org directory. `GetOrgProfile` 404s it on purpose:
//                        publishing it would put the SuperAdmin roster on a public page.
//   arkansas-serve       the REAL, BROWSABLE ORG. Same name, same description, same logo —
//                        this is the one the public profile is meant to show.
//
// Because they share a display name, anything holding a root membership (a SuperAdmin's, for
// instance) naturally builds a link to the org page using the id it has — root — and lands on
// a 404. The public face of root IS `arkansas-serve`, so resolve rather than refuse.
//
// This does NOT unhide root: the backend guard is untouched and root remains unservable. It
// only says which id to ask for when someone arrives holding the internal one.
//
// REQUIRES nothing.

'use strict';

const Orgs = (() => {
  // The internal platform partition. Never browsable.
  const ROOT_TENANT_ID = 'arkansas-serve-root';
  // Its public counterpart — an ordinary org like any other.
  const PUBLIC_ORG_ID  = 'arkansas-serve';

  const isRoot = (id) => String(id || '').trim().toLowerCase() === ROOT_TENANT_ID;

  // The id whose org page should actually be shown for `id`. Everything that isn't root
  // passes through unchanged.
  function canonicalOrgId(id) {
    return isRoot(id) ? PUBLIC_ORG_ID : id;
  }

  return { ROOT_TENANT_ID, PUBLIC_ORG_ID, isRoot, canonicalOrgId };
})();
