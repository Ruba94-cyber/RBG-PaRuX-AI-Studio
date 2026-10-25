"use strict";
function createDebouncer(fn, wait, timers) {
  let handle = null;
  return function debounced(...args) {
    if (handle !== null) {
      timers.clearTimeout(handle);
    }
    handle = timers.setTimeout(() => {
      handle = null;
      fn(...args);
    }, wait);
  };
}
module.exports = { createDebouncer };
