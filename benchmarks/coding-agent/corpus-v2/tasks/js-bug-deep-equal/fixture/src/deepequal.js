"use strict";
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== "object" || typeof b !== "object" || a === null || b === null) return false;
  for (const key of Object.keys(a)) {
    if (!deepEqual(a[key], b[key])) return false;
  }
  return true;
}
module.exports = { deepEqual };
