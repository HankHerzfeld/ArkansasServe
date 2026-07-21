// orgs.js — TEMPORARY NO-OP SHIM. Delete once the SWA deploy pipeline is unblocked.
//
// This file's real contents (the arkansas-serve-root → arkansas-serve alias) became obsolete
// when the two Arkansas Serve orgs were collapsed into one. Removing it should have been the
// end of it — but the deploy carrying that removal has now failed four times across two
// commits, while every other deploy the same day succeeded and Azure reports Static Web Apps
// healthy.
//
// The one structurally unusual thing in that changeset is this DELETION; every successful
// deploy that day only added or modified files. So the file is restored as an empty shim to
// test exactly that: if the deploy now succeeds, the deletion is implicated and this can be
// removed on its own afterwards. If it fails identically, the deletion is exonerated and the
// cause is elsewhere.
//
// NOTHING REFERENCES THIS FILE. Its script tags are already gone from organization.html and
// dashboard.html, so it is inert either way — it exists only so the payload contains no
// deletion.

'use strict';
