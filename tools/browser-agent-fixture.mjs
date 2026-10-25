// Node 20+, no npm packages. This is a disposable loopback-only web-UI
// acceptance fixture, NOT a browser or network sandbox. Use with PaRuX's
// dedicated developer browser; do not make it publicly reachable.
import { createServer } from "node:http";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const limit = 2048;
export async function startFixture() {
  const nonce = randomUUID().replaceAll("-", "");
  let origin = "";
  const js = [
    '"use strict";',
    'const nonce = ' + JSON.stringify(nonce) + ';',
    'const input = document.querySelector("#parux-input");',
    'const result = document.querySelector("#parux-result");',
    'document.querySelector("#parux-run").addEventListener("click", async () => {',
    ' result.textContent = "WORKING";',
    ' try {',
    '  const response = await fetch("/api/compute", {method:"POST",',
    '    headers:{"content-type":"application/json","x-parux-fixture":nonce},',
    '    body:JSON.stringify({value:input.value})});',
    '  const data = await response.json();',
    '  result.textContent = response.ok && data.accepted ? "ACCEPTED-" + nonce : "REJECTED";',
    ' } catch { result.textContent = "FAILED"; }',
    '});'
  ].join("\n");

  const server = createServer(async (req, res) => {
    res.setHeader("cache-control", "no-store");
    res.setHeader("x-content-type-options", "nosniff");
    res.setHeader("content-security-policy", "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'");
    if (req.method === "GET" && req.url === "/health") {
      res.writeHead(200, {"content-type":"application/json"});
      res.end(JSON.stringify({ready:true})); return;
    }
    if (req.method === "GET" && req.url === "/") {
      res.writeHead(200, {"content-type":"text/html; charset=utf-8"});
      res.end('<!doctype html><html lang="en"><head><meta charset="utf-8"><title>PaRuX Browser Acceptance</title><link rel="stylesheet" href="/style.css"></head>'
        + '<body><main><h1>PaRuX Browser Acceptance</h1><label for="parux-input">Input</label>'
        + '<input id="parux-input" autocomplete="off"><button id="parux-run" type="button">Run</button>'
        + '<output id="parux-result" aria-live="polite">PENDING</output></main><script src="/app.js"></script></body></html>');
      return;
    }
    if (req.method === "GET" && req.url === "/app.js") {
      res.writeHead(200, {"content-type":"text/javascript; charset=utf-8"});
      res.end(js); return;
    }
    if (req.method === "GET" && req.url === "/style.css") {
      res.writeHead(200, {"content-type":"text/css; charset=utf-8"});
      res.end('body{font:18px system-ui;margin:3rem}main{display:grid;gap:1rem;max-width:35rem}button{padding:.7rem}output{font-weight:bold}');
      return;
    }
    if (req.method === "POST" && req.url === "/api/compute") {
      if (req.headers.origin !== origin || req.headers["x-parux-fixture"] !== nonce
          || !String(req.headers["content-type"] || "").startsWith("application/json")) {
        res.writeHead(403); res.end(); return;
      }
      let raw = "";
      try {
        for await (const chunk of req) {
          raw += chunk.toString("utf8");
          if (raw.length > limit) { res.writeHead(413); res.end(); return; }
        }
        const parsed = JSON.parse(raw);
        const accepted = Object.keys(parsed).length === 1
          && parsed.value === "ready-" + nonce;
        res.writeHead(200, {"content-type":"application/json"});
        res.end(JSON.stringify({accepted}));
      } catch {
        res.writeHead(400); res.end();
      }
      return;
    }
    res.writeHead(404); res.end();
  });

  await new Promise((done, fail) => {
    server.once("error", fail);
    server.listen(0, "127.0.0.1", () => {
      server.off("error", fail); done();
    });
  });
  const address = server.address();
  origin = "http://127.0.0.1:" + address.port;
  return {server, url:origin + "/", nonce, expectedInput:"ready-" + nonce,
    expectedResult:"ACCEPTED-" + nonce,
    close:()=>new Promise((resolveClose,rejectClose) =>
      server.close(error => error ? rejectClose(error) : resolveClose()))};
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const fixture = await startFixture();
  // This stable single-line contract is consumed by a local acceptance driver.
  console.log("PARUX_BROWSER_FIXTURE=" + JSON.stringify({
    url:fixture.url, input:fixture.expectedInput, expected:fixture.expectedResult
  }));
  const shutdown = () => fixture.close().then(()=>process.exit(0),()=>process.exit(1));
  process.once("SIGINT", shutdown);
  process.once("SIGTERM", shutdown);
}
