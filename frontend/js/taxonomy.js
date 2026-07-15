// taxonomy.js — the org/event classification vocabulary, shared by every page that shows it.
//
// Mirrors backend/ArkansasServe.Functions/Models/ServiceCategories.cs and OrgTypes.cs. The
// server validates against its own copy, so this list is convenience, never authority: an
// entry missing here just does not appear in a dropdown; an entry missing THERE is a 400.
// Keep them in step — they are two files precisely because there is no build step to share
// one, so this is the trade being made knowingly.
//
// Previously each page hardcoded its own <option> list, which is how the org type dropdown
// came to write "organization" while seeded orgs said "Organization".

'use strict';

const Taxonomy = (() => {
  // Level 1: what KIND of org this is. Canonical casing is Capitalized — the dropdown used to
  // write lowercase, so read via isOrganization() rather than comparing strings.
  const ORG_TYPES = [
    { value: 'School',       label: 'School / District' },
    { value: 'JDC',          label: 'Juvenile Detention / Court' },
    { value: 'Organization', label: 'Community Organization' },
  ];

  // Level 2: what the org DOES. Shared with an event's category — one vocabulary, so a search
  // for "Elder Care" finds the org and its events, and they can never drift apart.
  //
  // Faith is deliberately absent: it is an attribute (faithBased), not a category. A church
  // running a food pantry is both, and one dropdown cannot hold two true things. The two
  // worship entries are for orgs whose service IS the faith.
  const SERVICE_CATEGORIES = [
    'Food & Nutrition',
    'Clothing & Basic Needs',
    'Housing & Shelter',
    'Elder Care',
    'Youth & Education',
    'Animal Welfare',
    'Environment & Conservation',
    'Parks & Recreation',
    'Health & Wellness',
    'Disaster Relief',
    'Worship & Congregational Life',
    'Religious Education & Ministry',
    'Community Development',
    'Arts & Culture',
    'Civic Engagement & Elections',
    'Political Parties & Campaigns',
    'Other',
  ];

  // Case-insensitive: live data carries both "organization" and "Organization", and a strict
  // comparison would silently skip whichever half it did not match.
  function isOrganization(type) {
    return String(type || '').trim().toLowerCase() === 'organization';
  }

  function orgTypeLabel(value) {
    const t = ORG_TYPES.find(o => o.value.toLowerCase() === String(value || '').trim().toLowerCase());
    return t ? t.label : (value || '');
  }

  // Fills a <select>. `selected` matches case-insensitively so a value stored before the
  // casing was settled still shows as chosen rather than silently resetting to blank —
  // which would look like the form losing the admin's answer.
  function fillSelect(sel, values, selected, placeholder = 'Select…') {
    if (!sel) return;
    sel.innerHTML = '';
    const blank = document.createElement('option');
    blank.value = ''; blank.textContent = placeholder;
    sel.appendChild(blank);
    values.forEach(v => {
      const value = typeof v === 'string' ? v : v.value;
      const label = typeof v === 'string' ? v : v.label;
      const o = document.createElement('option');
      o.value = value; o.textContent = label;
      if (String(selected || '').trim().toLowerCase() === value.toLowerCase()) o.selected = true;
      sel.appendChild(o);
    });
  }

  return { ORG_TYPES, SERVICE_CATEGORIES, isOrganization, orgTypeLabel, fillSelect };
})();
