import assert from "node:assert/strict";
import test from "node:test";
import { startFixture } from "./browser-agent-fixture.mjs";

test("loopback disposable browser fixture enforces nonce, origin and asynchronous state contract", async () => {
  const fixture = await startFixture();
  try {
    const url = new URL(fixture.url);
    assert.equal(url.hostname, "127.0.0.1");
    assert.match(fixture.expectedInput, /^ready-[a-f0-9]{32}$/);
    assert.equal(fixture.expectedResult, fixture.expectedInput.replace("ready-", "ACCEPTED-"));

    const html = await fetch(fixture.url);
    assert.equal(html.status, 200);
    assert.match(html.headers.get("content-security-policy"), /connect-src 'self'/);
    assert.equal(html.headers.get("cache-control"), "no-store");
    const page = await html.text();
    for (const id of ["parux-input", "parux-run", "parux-result"]) {
      assert.ok(page.includes('id="' + id + '"'), "Missing interactive fixture control " + id);
    }
    const js = await (await fetch(fixture.url + "app.js")).text();
    assert.match(js, /addEventListener\("click"/);
    assert.match(js, /fetch\("\/api\/compute"/);
    assert.ok(!js.includes("eval("), "Fixture must not evaluate arbitrary input");

    const endpoint = fixture.url + "api/compute";
    const options = (value, origin = url.origin, token = fixture.nonce) => ({
      method:"POST",
      headers: {
        origin,
        "x-parux-fixture":token,
        "content-type":"application/json"
      },
      body:JSON.stringify({value})
    });
    const correct = await fetch(endpoint, options(fixture.expectedInput));
    assert.equal(correct.status, 200);
    assert.deepEqual(await correct.json(), {accepted:true});

    const incorrect = await fetch(endpoint, options("wrong"));
    assert.equal(incorrect.status, 200);
    assert.deepEqual(await incorrect.json(), {accepted:false});

    assert.equal((await fetch(endpoint, options(fixture.expectedInput, "http://evil.invalid"))).status, 403);
    assert.equal((await fetch(endpoint, options(fixture.expectedInput, url.origin, "wrong-nonce"))).status, 403);
    assert.equal((await fetch(fixture.url + "not-found")).status, 404);
  } finally {
    await fixture.close();
  }
});

test("every fixture run uses a distinct nonce and independently owned server", async () => {
  const a = await startFixture();
  const b = await startFixture();
  try {
    assert.notEqual(a.nonce,b.nonce);
    assert.notEqual(a.url,b.url);
    assert.equal((await fetch(a.url + "health")).status,200);
    assert.equal((await fetch(b.url + "health")).status,200);
  } finally {
    await a.close();
    await b.close();
  }
});
