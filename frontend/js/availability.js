// availability.js — how many spots an event actually has left.
//
// A shifted event carries TWO capacity systems, and the server enforces BOTH:
// `AdjustSlotsAsync` refuses a sign-up that would push `currentSlots` past `maxSlots`
// *or* a shift's `filled` past its `capacity`. The card and detail page used to read
// only the overall pair, so a shifted event advertised a number that had nothing to do
// with the per-shift counts printed directly beneath it — the event could show
// "10 spots left" while every shift was full.
//
// The real answer is the tighter of the two gates: you can only join by taking a place
// in some shift, and the overall cap still applies on top of that.
//
//   remaining = min(overall remaining, sum of per-shift remaining)
//
// A capacity of 0 means "uncapped" on both models, represented here as Infinity so it
// falls out of the min() naturally. That makes the no-shift case reduce to exactly the
// old behaviour (no shifts -> the shift term is Infinity -> min is the overall
// remaining), so this is a generalisation rather than a branch on "does it have shifts".
//
// REQUIRES nothing. Pure functions over an event object as the API returns it.

'use strict';

const Availability = (() => {
  // Both `maxSlots` and a shift's `capacity` use 0 to mean "no cap".
  const UNCAPPED = Infinity;

  // Spots left in one shift. Uncapped shifts are Infinity, so a single uncapped shift
  // makes the shift term unbounded and the overall cap becomes the only limit.
  function shiftRemaining(shift) {
    const capacity = shift?.capacity || 0;
    return capacity > 0 ? Math.max(0, capacity - (shift.filled || 0)) : UNCAPPED;
  }

  // The event-level gate, ignoring shifts.
  function overallRemaining(evt) {
    return evt?.maxSlots > 0 ? Math.max(0, evt.maxSlots - (evt.currentSlots || 0)) : UNCAPPED;
  }

  // The shift-level gate: how many people could still be placed across all shifts.
  // No shifts at all means this gate does not apply, not that there is no room.
  function shiftsRemaining(evt) {
    const shifts = evt?.shifts || [];
    if (!shifts.length) return UNCAPPED;
    return shifts.reduce((sum, s) => sum + shiftRemaining(s), 0);
  }

  // Spots left, honouring whichever gate binds first.
  function remaining(evt) {
    return Math.min(overallRemaining(evt), shiftsRemaining(evt));
  }

  function isUncapped(evt) { return remaining(evt) === UNCAPPED; }

  // An uncapped event always has room, matching how the events filter has always
  // treated `maxSlots === 0`.
  function hasRoom(evt) { return remaining(evt) > 0; }

  function hasShifts(evt) { return ((evt?.shifts || []).length) > 0; }

  // Display string, or null when there is no number worth showing (uncapped).
  // A shifted event names the shift count, because the total is a sum across shifts
  // and would otherwise look inconsistent with any single shift's row.
  function label(evt) {
    const left = remaining(evt);
    if (left === UNCAPPED) return null;
    const spots = `${left} spot${left === 1 ? '' : 's'} left`;
    if (!hasShifts(evt)) return spots;
    const count = evt.shifts.length;
    return `${spots} across ${count} shift${count === 1 ? '' : 's'}`;
  }

  // Sortable key for the DataTables mirror, which compares strings. Uncapped events
  // sort as the roomiest rather than as zero.
  function sortKey(evt) {
    const left = remaining(evt);
    return left === UNCAPPED ? '999999' : String(left);
  }

  return {
    UNCAPPED,
    shiftRemaining,
    overallRemaining,
    shiftsRemaining,
    remaining,
    isUncapped,
    hasRoom,
    hasShifts,
    label,
    sortKey,
  };
})();
