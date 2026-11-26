"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { FALLBACK_URL, normalizeAppUrl, resolveAppUrl } = require("../src/config.cjs");

test("https addresses are accepted and tidied", () => {
  assert.equal(normalizeAppUrl("https://app.example.com/"), "https://app.example.com");
  assert.equal(normalizeAppUrl(" https://app.example.com/daksha/?x=1#y "), "https://app.example.com/daksha");
});

test("http is only allowed for localhost", () => {
  assert.equal(normalizeAppUrl("http://localhost:3000"), "http://localhost:3000");
  assert.equal(normalizeAppUrl("http://127.0.0.1:3000/"), "http://127.0.0.1:3000");
  assert.throws(() => normalizeAppUrl("http://app.example.com"), /https/);
});

test("addresses with credentials, other schemes or nothing are refused", () => {
  assert.throws(() => normalizeAppUrl("https://me:pw@app.example.com"), /user name/);
  assert.throws(() => normalizeAppUrl("file:///etc/passwd"));
  assert.throws(() => normalizeAppUrl("javascript:alert(1)"));
  assert.throws(() => normalizeAppUrl(""));
  assert.throws(() => normalizeAppUrl(undefined));
});

test("the environment beats the settings file, which beats the release default", () => {
  const packaged = { appUrl: "https://release.example.com" };
  assert.equal(resolveAppUrl({ env: { DAKSHA_APP_URL: "https://env.example.com" }, fileConfig: { appUrl: "https://file.example.com" }, packaged }).url, "https://env.example.com");
  assert.equal(resolveAppUrl({ env: {}, fileConfig: { appUrl: "https://file.example.com" }, packaged }).url, "https://file.example.com");
  const r = resolveAppUrl({ env: {}, fileConfig: null, packaged });
  assert.equal(r.url, "https://release.example.com");
  assert.equal(r.origin, "https://release.example.com");
  assert.equal(r.source, "this release");
});

test("a bad address falls back with a warning instead of stopping the app", () => {
  const r = resolveAppUrl({ env: { DAKSHA_APP_URL: "http://evil.example.com" }, fileConfig: null, packaged: { appUrl: "https://release.example.com" } });
  assert.equal(r.url, "https://release.example.com");
  assert.match(r.warning, /environment/);
  const none = resolveAppUrl({ env: { DAKSHA_APP_URL: "nonsense" }, fileConfig: null, packaged: null });
  assert.equal(none.url, FALLBACK_URL);
  assert.ok(none.warning);
});
