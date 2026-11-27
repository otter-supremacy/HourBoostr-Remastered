import assert from "node:assert/strict";
import test from "node:test";
import worker from "./worker.mjs";

const env = {
  PUBLIC_HOST: "members.example.test",
  MEMBER_ORIGIN: "https://origin.example.test",
  ORIGIN_KEY: "fixture",
  REGISTRATION_OPEN: "1",
};

test("only the configured public hostname and member routes are exposed", async () => {
  for (const path of ["/control/info", "/internal/members", "/auth/test/alice", "/__hbr_members/", "/api/member/chat/../backup", "/api/member/%2finternal", "https://elsewhere.example.test/"]) {
    const response = await worker.fetch(new Request(new URL(path, "https://members.example.test")), env);
    assert.equal(response.status, 404);
  }
  const privacy = await worker.fetch(new Request("https://members.example.test/privacy"), env);
  assert.match(await privacy.text(), /uses simulated Steam/i);
});

test("the landing page can be indexed while account and API routes are private", async t => {
  const origin = t.mock.method(globalThis, "fetch", async () => new Response("dashboard"));
  const staticEnv = { ...env, ASSETS: { fetch(request) {
    assert.equal(new URL(request.url).pathname, "/");
    assert.equal(request.headers.has("Cookie"), false);
    return new Response("public landing");
  } } };
  const home = await worker.fetch(new Request("https://members.example.test/", { headers: { Cookie: "session=private" } }), staticEnv);
  assert.equal(await home.text(), "public landing");
  assert.equal(home.headers.get("X-Robots-Tag"), null);
  const app = await worker.fetch(new Request("https://members.example.test/signup"), staticEnv);
  assert.equal(app.headers.get("X-Robots-Tag"), "noindex");
  assert.equal(origin.mock.callCount(), 1);
  assert.equal(new URL(origin.mock.calls[0].arguments[0]).pathname, "/__hbr_members/signup");
});

test("forwards login redirects and cookies with an authenticated origin request", async t => {
  t.mock.method(globalThis, "fetch", async (url, options) => {
    assert.equal(url.href, "https://origin.example.test/__hbr_members/auth/google");
    assert.equal(options.redirect, "manual");
    assert.equal(options.headers.get("Cookie"), "fixture=value");
    assert.equal(options.headers.get("X-HBR-Origin-Key"), env.ORIGIN_KEY);
    assert.equal(options.headers.get("X-HBR-Edge-IP"), "192.0.2.42");
    assert.equal(options.headers.get("Authorization"), null);
    const headers = new Headers({ Location: "https://accounts.google.com/" });
    headers.append("Set-Cookie", "one=1; Secure; HttpOnly");
    headers.append("Set-Cookie", "two=2; Secure; HttpOnly");
    return new Response(null, { status: 302, headers });
  });
  const response = await worker.fetch(new Request("https://members.example.test/auth/google", {
    headers: { Cookie: "fixture=value", Authorization: "Bearer untrusted", "X-HBR-Origin-Key": "untrusted", "CF-Connecting-IP": "192.0.2.42" }
  }), env);
  assert.equal(response.status, 302);
  assert.equal(response.headers.get("Location"), "https://accounts.google.com/");
  assert.equal(response.headers.getSetCookie().length, 2);
  assert.equal(response.headers.get("Cache-Control"), "private, no-store");
});

test("the former hostname redirects public pages without carrying OAuth parameters", async () => {
  const legacy = { ...env, REDIRECT_TO: "https://hourboostr.pages.dev" };
  const callback = await worker.fetch(new Request("https://members.example.test/auth/callback?code=fixture&state=fixture"), legacy);
  assert.equal(callback.status, 302);
  assert.equal(callback.headers.get("Location"), "https://hourboostr.pages.dev/login");
  const admin = await worker.fetch(new Request("https://members.example.test/control/info"), legacy);
  assert.equal(admin.status, 404);
  const mutation = await worker.fetch(new Request("https://members.example.test/api/member/start", { method: "POST" }), legacy);
  assert.equal(mutation.status, 404);
});

test("forwards only supported member key methods and masks uncached responses", async t => {
  const origin = t.mock.method(globalThis, "fetch", async () => new Response(null, { status: 204 }));
  for (const [method, path] of [["PUT", "key/gpt"], ["DELETE", "key/qwen"], ["POST", "preview"]]) {
    const response = await worker.fetch(new Request("https://members.example.test/api/member/chat/" + path, { method }), env);
    assert.equal(response.status, 204);
    assert.match(response.headers.get("Cache-Control"), /no-store/);
  }
  for (const path of ["key/gpt", "key/qwen", "key/invalid"]) {
    const response = await worker.fetch(new Request("https://members.example.test/api/member/chat/" + path), env);
    assert.equal(response.status, 404);
  }
  assert.equal(origin.mock.callCount(), 3);
});
