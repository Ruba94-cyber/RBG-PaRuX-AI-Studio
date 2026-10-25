"use strict";
function escapeHtml(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
function lookup(data, path) {
  let cur = data;
  for (const part of path.split(".")) {
    if (cur === undefined || cur === null) return undefined;
    cur = cur[part];
  }
  return cur;
}
function render(template, data) {
  return template.replace(/\{\{\s*([\w.]+)\s*(?:\|([^}]*))?\}\}/g, (m, path, fallback) => {
    const value = lookup(data, path);
    if (value === undefined || value === null) {
      return fallback === undefined ? "" : escapeHtml(fallback.trim());
    }
    return escapeHtml(String(value));
  });
}
module.exports = { render };
