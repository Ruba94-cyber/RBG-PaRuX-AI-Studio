import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));
const read = p => fs.readFileSync(path.join(root,p),"utf8");
const req = (c,m) => { if(!c) throw new Error(m); };
const id = process.argv[2];

if (id === "web-drawer-accessibility") {
  const html=read("Web/index.html"), css=read("Web/styles.css"), app=read("Web/app.mjs");
  req(/aria-controls=["']conversation-drawer["']/.test(html),"toggle controls drawer");
  req(/aria-expanded=/.test(html),"expanded state exposed");
  req(!/@media[^]*#conversation-drawer\s*\{\s*display:\s*none/.test(css),"narrow screen must not permanently hide drawer");
  req(/drawer-toggle/.test(app) && /hidden/.test(app),"toggle behavior");
} else if (id === "web-keyboard-drawer") {
  const app=read("Web/app.mjs");
  req(/Escape/.test(app) && /focus\(/.test(app),"escape/focus recovery");
} else if (id === "winui-scroll-layout") {
  const x=read("WinUI/MainPage.xaml");
  req(/ScrollViewer/.test(x) && /WorkspaceList/.test(x),"workspace navigation scrollable");
} else if (id === "winui-keyboard-navigation") {
  const c=read("WinUI/MainPage.xaml.cs");
  req(/Arrow(Up|Down)/.test(c) && /Escape/.test(c) && /Enter/.test(c),"keyboard keys supported");
} else if (id === "winui-chat-home") {
  const c=read("WinUI/MainPage.xaml.cs");
  req(/=>\s*"Chat"/.test(c) || /return\s+"Chat"/.test(c),"Chat remains home");
} else if (id === "winui-normal-advanced") {
  const c=read("WinUI/MainPage.xaml.cs");
  req(/workspace\s*==\s*"Chat"/.test(c) || /"Chat".*true/.test(c),"Chat visible in Normal");
  req(/advanced/.test(c),"advanced mode considered");
} else if (id === "cppqt-ring-buffer") {
  const c=read("CppQt/RingBuffer.cpp");
  req(/erase|head_|start_|\%\s*capacity_/.test(c),"bounded wrap/eviction implemented");
} else if (id === "cppqt-zero-capacity") {
  const c=read("CppQt/RingBuffer.cpp");
  req(/capacity_\s*==\s*0|capacity\s*==\s*0/.test(c),"zero capacity guarded");
} else if (id === "cppqt-queued-signal") {
  const c=read("CppQt/MainWindow.cpp");
  req(/Qt::QueuedConnection/.test(c),"queued connection explicit");
  req(/&[A-Za-z0-9_:]+::[A-Za-z0-9_]+/.test(c),"typed signal/slot syntax");
} else if (id === "cppqt-qml-scroll") {
  const q=read("CppQt/Settings.qml");
  req(/ListView|Flickable/.test(q),"scrollable control");
  req(/Accessible\.name/.test(q),"accessible name");
} else if (id === "long-context-token-extract") {
  const obj=JSON.parse(read("LongContext/result.json"));
  const expected=["ORBIT-7319","LEDGER-2048","GAMING-HARD-OFF-55","STOP-BOUNDED-901","BACKUP-ISOLATED-42","FINAL-ACCEPT-77"];
  req(JSON.stringify(obj.tokens)===JSON.stringify(expected),"all six tokens in order");
} else if (id === "long-context-policy-synthesis") {
  const obj=JSON.parse(read("LongContext/policies.json"));
  req(obj.home==="Chat" && obj.strictLocal==="no-inference-egress","core policies");
  req(obj.resumePolicy==="skip-completed-retry-failed-once" && obj.pairing==="explicit-authorized-only","recovery/pairing policies");
} else if (id === "artifact-accessible-report") {
  const h=read("Artifact/report.html");
  req(/<main\b/.test(h) && /<h1\b/.test(h),"main heading");
  req(/<table\b/.test(h) && /<caption\b/.test(h) && /scope=["']col["']/.test(h),"semantic table");
  req(/aria-live=["']polite["']/.test(h),"status live region");
  req(!/<script\b[^>]*>\s*[^<]/.test(h),"no inline executable script");
} else {
  throw new Error(`unknown case ${id}`);
}
console.log("PASS");
