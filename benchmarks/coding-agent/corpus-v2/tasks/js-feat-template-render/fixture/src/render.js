"use strict";
function render(template, data) {
  return template.replace(/\{\{(\w+)\}\}/g, (m, key) => String(data[key]));
}
module.exports = { render };
