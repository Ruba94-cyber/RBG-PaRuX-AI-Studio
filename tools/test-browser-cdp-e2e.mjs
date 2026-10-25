// Real-browser infrastructure acceptance, opt-in on an already-installed
// Chromium/Edge binary. Node 22+ (built-in WebSocket), no npm dependencies.
// This is NOT a substitute for driving PaRuX's own C# browser UI on Windows.
// Operates only on the disposable nonce-scoped 127.0.0.1 fixture.
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { startFixture } from "./browser-agent-fixture.mjs";

if (typeof WebSocket !== "function")
  throw Error("Node 22+ with built-in WebSocket is required.");
const executable = process.argv[2] || process.env.PARUX_BROWSER_TEST_EXECUTABLE;
if (!executable)
  throw Error("Provide the installed Edge/Chromium executable as argument 1 or PARUX_BROWSER_TEST_EXECUTABLE.");
const fixture = await startFixture();
const profile = await mkdtemp(join(tmpdir(), "parux-browser-acceptance-"));
let browser;
let socket;
let nextId = 0;
let childDetected = false;
const pending = new Map();
const origin = new URL(fixture.url).origin;
let blockedRequests = 0;

function send(method, params = {}) {
  if (childDetected) return Promise.reject(Error("Uncontrolled child target is not admitted."));
  if (!socket || socket.readyState !== WebSocket.OPEN)
    return Promise.reject(Error("Browser CDP socket is unavailable."));
  return new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    socket.send(JSON.stringify({ id, method, params }));
    const timeout = setTimeout(() => {
      if (pending.delete(id)) reject(Error("CDP command timed out: " + method));
    }, 15000);
    timeout.unref?.();
  });
}

function receive(event) {
  let packet;
  try { packet = JSON.parse(event.data); } catch { return; }
  if (typeof packet.id === "number") {
    const waiter = pending.get(packet.id);
    if (!waiter) return;
    pending.delete(packet.id);
    if (packet.error) waiter.reject(Error("CDP rejected command."));
    else waiter.resolve(packet.result || {});
    return;
  }
  if (packet.method === "Target.attachedToTarget") {
    childDetected = true;
    for (const waiter of pending.values())
      waiter.reject(Error("Uncontrolled browser target appeared."));
    pending.clear();
    try { browser?.kill(); } catch {}
    return;
  }
  if (packet.method === "Fetch.requestPaused") {
    const requestId = packet.params?.requestId;
    const address = packet.params?.request?.url;
    if (!requestId || !address) return; // leave malformed requests paused
    let allowed = false;
    try { allowed = new URL(address).origin === origin; } catch {}
    if (!allowed) blockedRequests++;
    void send(allowed ? "Fetch.continueRequest" : "Fetch.failRequest",
      allowed ? { requestId } : { requestId, errorReason: "BlockedByClient" })
      .catch(() => {}); // leave intercepted request paused on CDP failure
  }
}

async function until(attempt, description, ms = 15000) {
  const deadline = Date.now() + ms;
  while (Date.now() < deadline) {
    try {
      const result = await attempt();
      if (result) return result;
    } catch { /* transient startup or not-yet-rendered DOM */ }
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  throw Error("Timed out waiting for " + description);
}

async function evaluate(expression) {
  const packet = await send("Runtime.evaluate", {
    expression, returnByValue: true, awaitPromise: true, userGesture: false
  });
  if (packet.exceptionDetails || packet.result?.subtype === "error")
    throw Error("Browser JavaScript evaluation failed.");
  return packet.result?.value;
}

try {
  const flags = [
    "--headless=new", "--remote-debugging-address=127.0.0.1",
    "--remote-debugging-port=0", "--user-data-dir=" + profile,
    "--no-first-run", "--no-default-browser-check", "--disable-sync",
    "--disable-component-update", "--disable-background-networking",
    "--disable-default-apps",
    "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE localhost, EXCLUDE 127.0.0.1, EXCLUDE [::1]",
    "about:blank"
  ];
  browser = spawn(executable, flags, { stdio: "ignore", windowsHide: true });
  const port = await until(async () => {
    const content = await readFile(join(profile, "DevToolsActivePort"), "utf8");
    const parsed = Number(content.split("\n")[0]);
    return Number.isInteger(parsed) && parsed > 0 && parsed <= 65535 && parsed;
  }, "private CDP listener");

  const pageSocket = await until(async () => {
    const list = await (await fetch("http://127.0.0.1:" + port + "/json/list")).json();
    const page = list.find(entry => entry.type === "page" && entry.webSocketDebuggerUrl);
    if (!page) return false;
    const address = new URL(page.webSocketDebuggerUrl);
    return address.protocol === "ws:" && address.hostname === "127.0.0.1"
      && Number(address.port) === port ? address.href : false;
  }, "private page target");

  socket = new WebSocket(pageSocket);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  socket.addEventListener("message", receive);

  await send("Target.setAutoAttach", {
    autoAttach: true, waitForDebuggerOnStart: true, flatten: true
  });
  await send("Page.enable");
  await send("Runtime.enable");
  await send("Fetch.enable", {
    patterns: [{ urlPattern: "*", requestStage: "Request" }]
  });

  const navigation = await send("Page.navigate", { url: fixture.url });
  if (navigation.errorText)
    throw Error("Browser refused fixture navigation: " + navigation.errorText);
  // The button exists as soon as the HTML is parsed, but its click handler only after the
  // intercepted /app.js has been admitted and executed; clicking earlier is a lost click.
  await until(() => evaluate('document.readyState === "complete"'
      + ' && document.querySelector("#parux-run") !== null'),
    "browser fixture DOM and script");

  assert.equal(await evaluate('document.querySelector("#parux-result").textContent'),
    "PENDING", "Before action the result must not be marked accepted.");
  assert.equal(await evaluate('(() => { const el=document.querySelector("#parux-input");'
      + 'el.value=' + JSON.stringify(fixture.expectedInput)
      + ';el.dispatchEvent(new Event("input",{bubbles:true}));return el.value;})()'),
    fixture.expectedInput);
  assert.equal(await evaluate('(() => { document.querySelector("#parux-run").click();'
      + 'return true; })()'), true);
  await until(() => evaluate('document.querySelector("#parux-result").textContent')
    .then(text => text === fixture.expectedResult),
    "independent async DOM postcondition").catch(async error => {
      let observed = "<unreadable>";
      try { observed = String(await evaluate('document.querySelector("#parux-result").textContent')); } catch {}
      throw Error(error.message + " (observed result: " + observed.slice(0, 64) + ")");
    });
  assert.equal(await evaluate('document.querySelector("#parux-result").textContent'),
    fixture.expectedResult);
  assert.equal(childDetected, false);
  console.log("BROWSER CDP E2E PASS: actual Chromium input, click, async DOM assertion, "
    + "private profile and guarded root-page requests; denied unrelated requests="
    + blockedRequests);
} finally {
  for (const waiter of pending.values()) waiter.reject(Error("Browser test shutting down."));
  pending.clear();
  try { socket?.close(); } catch {}
  if (browser?.pid) {
    try {
      if (process.platform === "win32")
        spawnSync("taskkill", ["/PID", String(browser.pid), "/T", "/F"], { stdio: "ignore" });
      else browser.kill("SIGTERM");
    } catch {}
  }
  // In a broken browser the fixture may have open keepalive sockets.
  await fixture.close();
  await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
}
