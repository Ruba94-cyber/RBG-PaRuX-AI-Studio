"use strict";
function parseQuery(qs) {
  const out = {};
  for (const pair of qs.split("&")) {
    const [k, v] = pair.split("=");
    out[k] = v;
  }
  return out;
}
module.exports = { parseQuery };
