"use strict";
function flatten(arr, depth = 1) {
  const out = [];
  for (const item of arr) {
    if (Array.isArray(item)) out.push(...flatten(item, depth));
    else out.push(item);
  }
  return out;
}
module.exports = { flatten };
