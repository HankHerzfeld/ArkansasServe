// datatables.js — house wiring for DataTables.
//
// PHASE 1: CLIENT-SIDE ONLY, and deliberately so. Every row is shipped and the browser
// does the searching/sorting/paging. That is correct at tens of rows and wrong at
// thousands: once an org approaches four figures this must move to server-side
// processing (see "DataTables phase 2" at the end of docs/roadmap.md). The upgrade is
// confined to how a table gets its rows — `data:` becomes `ajax:` + `serverSide: true` —
// so columns, filters and markup below carry over unchanged.
//
// The library is vendored (js/vendor/jquery.min.js, js/vendor/dataTables.min.js) rather
// than pulled from a CDN because the CSP allows script-src 'self' only.
//
// REQUIRES jquery.min.js + dataTables.min.js loaded first.

'use strict';

const DT = (() => {
  // What the app keeps for itself vs. what DataTables owns.
  //
  // The pages already have their own search box, domain dropdowns and "n of m" counter,
  // built before DataTables and matching the app's look — so DataTables' own search box
  // and info line are turned OFF and the existing controls are wired into its API
  // instead. What DataTables actually adds here is column SORTING and PAGING, which the
  // app had nowhere.
  const HOUSE_DEFAULTS = {
    paging: true,
    pageLength: 25,
    lengthChange: false,
    info: false,        // pages render their own "n of m"
    searching: true,    // engine on; its built-in input is suppressed via `layout` below
    ordering: true,
    autoWidth: false,
    // DataTables 2 layout API. Only paging is rendered; everything else is ours.
    layout: {
      topStart: null,
      topEnd: null,
      bottomStart: null,
      bottomEnd: 'paging',
    },
    language: {
      zeroRecords: '',   // pages own their empty state
      emptyTable: '',
    },
  };

  // Custom row filters, keyed by table id. DataTables' ext.search is GLOBAL — every
  // registered function runs for every table on the page — so each one must first check
  // it is looking at its own table or tables would silently filter each other.
  const rowFilters = new Map();
  let extRegistered = false;

  function ensureExtSearch() {
    if (extRegistered) return;
    extRegistered = true;
    DataTable.ext.search.push((settings, searchData, dataIndex, rowData, counter) => {
      const fn = rowFilters.get(settings.nTable.id);
      return fn ? fn(dataIndex, settings) !== false : true;
    });
  }

  // Mount DataTables over a table the page has ALREADY rendered.
  //
  // Initialising over existing DOM (rather than handing DataTables a `data:` array) is
  // deliberate: these tables contain live controls — selects, inputs, checkboxes, Save
  // buttons — with listeners already attached. DataTables moves those existing <tr>
  // nodes when sorting or paging rather than re-creating them, so the listeners survive.
  // Building rows from a data array would rebuild the DOM and drop them.
  //
  // Re-mounting is safe: any previous instance on the same table is destroyed first,
  // which is what the render functions rely on when their underlying data reloads.
  function mount(tableId, opts = {}) {
    const el = document.getElementById(tableId);
    if (!el) return null;

    destroy(tableId);
    ensureExtSearch();

    if (typeof opts.rowFilter === 'function') rowFilters.set(tableId, opts.rowFilter);
    else rowFilters.delete(tableId);

    const config = Object.assign({}, HOUSE_DEFAULTS, opts.dataTables || {});
    return new DataTable('#' + tableId, config);
  }

  function destroy(tableId) {
    const el = document.getElementById(tableId);
    if (!el) return;
    if (DataTable.isDataTable('#' + tableId)) {
      new DataTable('#' + tableId).destroy();
    }
    rowFilters.delete(tableId);
  }

  function instance(tableId) {
    return DataTable.isDataTable('#' + tableId) ? new DataTable('#' + tableId) : null;
  }

  // How many rows survive the current search + filters. Pages use this for their own
  // "n of m" counter, which DataTables' info line would otherwise duplicate.
  function visibleCount(tableId) {
    const dt = instance(tableId);
    return dt ? dt.rows({ search: 'applied' }).count() : 0;
  }

  return { mount, destroy, instance, visibleCount, HOUSE_DEFAULTS };
})();
