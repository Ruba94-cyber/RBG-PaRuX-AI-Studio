import { reduceState, parseNdjsonChunks, sanitizeArtifactHref } from "./app.mjs";

const id = process.argv[2];
const require = (cond, msg) => { if (!cond) throw new Error(msg); };

if (id === "web-state-selection") {
  const before = { conversations:[{id:"a"},{id:"b"}], selectedId:"b" };
  const after = reduceState(before,{type:"replaceConversations",items:[{id:"a"},{id:"b"},{id:"c"}]});
  require(after.selectedId === "b","preserve selected id when still present after reorder");
  require(before.selectedId === "b" && before.conversations.length === 2,"previous state remains unchanged");
} else if (id === "web-stream-parser") {
  const rows = parseNdjsonChunks(['{"a":1}\n{"b"', ':2}\n\n{"done":', 'true}\n']);
  require(JSON.stringify(rows) === JSON.stringify([{a:1},{b:2},{done:true}]),"chunk boundary parser");
} else if (id === "web-artifact-sanitize") {
  require(sanitizeArtifactHref("javascript:alert(1)") === null,"javascript blocked");
  require(sanitizeArtifactHref("data:text/html,x") === null,"data blocked");
  require(sanitizeArtifactHref("/api/v1/artifacts/1") === "/api/v1/artifacts/1","relative artifact allowed");
} else {
  throw new Error(`unknown case ${id}`);
}
console.log("PASS");
