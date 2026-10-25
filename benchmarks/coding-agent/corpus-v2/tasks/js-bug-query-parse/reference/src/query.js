"use strict";
function dec(s) {
  return decodeURIComponent(s.replace(/\+/g, " "));
}
function parseQuery(qs) {
  const out = {};
  const body = qs.startsWith("?") ? qs.slice(1) : qs;
  if (body === "") return out;
  for (const pair of body.split("&")) {
    if (pair === "") continue;
    const i = pair.indexOf("=");
    const k = dec(i < 0 ? pair : pair.slice(0, i));
    const v = i < 0 ? "" : dec(pair.slice(i + 1));
    if (Object.prototype.hasOwnProperty.call(out, k)) {
      out[k] = [].concat(out[k], v);
    } else {
      out[k] = v;
    }
  }
  return out;
}
module.exports = { parseQuery };
