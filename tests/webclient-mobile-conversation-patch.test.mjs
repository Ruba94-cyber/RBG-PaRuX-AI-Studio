import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const read = (relative) => fs.readFileSync(path.join(root, relative), "utf8");

const js = read("src/PaRuX.Server/WebClient/mobile-conversation-patch.js");
const css = read("src/PaRuX.Server/WebClient/mobile-conversation-patch.css");
const endpoint = read("src/PaRuX.Server/WebClientEndpoint.cs");
const project = read("src/PaRuX.Server/PaRuX.Server.csproj");

assert.match(js, /aria-controls/);
assert.match(js, /aria-expanded/);
assert.match(js, /event\.key\s*!==\s*"Escape"/);
assert.match(js, /toggle\.focus\(\)/);
assert.match(js, /newChat\.focus\(\)/);
assert.match(js, /conversationList\.addEventListener\("click"/);
assert.match(js, /mobile-conversations-open/);
assert.match(js, /matchMedia\("\(max-width: 800px\)"\)/);

assert.match(css, /@media \(max-width: 800px\)/);
assert.match(css, /#chatpage\.active \.convlist\s*\{[^}]*display:\s*block/s);
assert.match(css, /body\.mobile-conversations-open #chatpage\.active \.convlist/);
assert.match(css, /transform:\s*translateX\(0\)/);
assert.match(css, /prefers-reduced-motion/);

assert.match(endpoint, /MobileConversationStyleResourceName/);
assert.match(endpoint, /MobileConversationScriptResourceName/);
assert.match(endpoint, /parux-mobile-conversation-style/);
assert.match(endpoint, /parux-mobile-conversation-script/);
assert.match(endpoint, /BuildIndexHtml/);

assert.match(project, /mobile-conversation-patch\.css/);
assert.match(project, /mobile-conversation-patch\.js/);
assert.match(project, /EmbeddedResource/);

console.log("webclient mobile conversation drawer contract: PASS");
