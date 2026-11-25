"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { classifyNavigation, classifyWindowOpen, deepLinkToPath, isSafeExternal, pageUrl } = require("../src/policy.cjs");

const ORIGIN = "https://app.example.com";

test("sign-in popups (about:blank) and same-site windows open in the app; other web links go to the browser", () => {
  assert.equal(classifyWindowOpen("about:blank", ORIGIN), "popup");
  assert.equal(classifyWindowOpen("https://app.example.com/apps/youtube", ORIGIN), "popup");
  assert.equal(classifyWindowOpen("https://github.com/x", ORIGIN), "external");
  assert.equal(classifyWindowOpen("mailto:a@b.co", ORIGIN), "external");
});

test("dangerous schemes are never opened", () => {
  for (const url of ["file:///etc/passwd", "javascript:alert(1)", "data:text/html,hi", "vscode://x", "daksha://apps", "not a url"]) {
    assert.equal(classifyWindowOpen(url, ORIGIN), "deny", url);
    assert.equal(isSafeExternal(url), false, url);
  }
});

test("the main window stays on the app and sends other links to the browser", () => {
  assert.equal(classifyNavigation("https://app.example.com/apps", ORIGIN, false), "allow");
  assert.equal(classifyNavigation("https://accounts.google.com/x", ORIGIN, false), "external");
  assert.equal(classifyNavigation("file:///tmp/offline.html", ORIGIN, false), "allow");
  assert.equal(classifyNavigation("javascript:alert(1)", ORIGIN, false), "deny");
  assert.equal(classifyNavigation("https://app.example.com.evil.io/", ORIGIN, false), "external"); // not the same origin
});

test("a sign-in popup may visit any https provider page but not plain http", () => {
  assert.equal(classifyNavigation("https://accounts.google.com/o/oauth2/v2/auth", ORIGIN, true), "allow");
  assert.equal(classifyNavigation("https://login.microsoftonline.com/", ORIGIN, true), "allow");
  assert.equal(classifyNavigation("http://evil.example.com/", ORIGIN, true), "deny");
  assert.equal(classifyNavigation("https://app.example.com/apps/youtube?connected=1", ORIGIN, true), "allow");
});

test("daksha:// links map to pages of the app", () => {
  assert.equal(deepLinkToPath("daksha://apps/youtube"), "/apps/youtube");
  assert.equal(deepLinkToPath("daksha://apps/youtube?connected=1"), "/apps/youtube?connected=1");
  assert.equal(deepLinkToPath("daksha://dashboard"), "/dashboard");
  assert.equal(deepLinkToPath("daksha://assistant/"), "/assistant");
});

test("daksha:// links that are not ours or look unsafe are ignored", () => {
  for (const url of ["https://app.example.com/apps", "daksha://../etc/passwd", "daksha://apps//evil.com", "daksha://apps/<script>", "garbage"]) assert.equal(deepLinkToPath(url), null, url);
  // Encoded dots are resolved by the URL parser itself, and what is left is always a plain in-app path.
  const resolved = deepLinkToPath("daksha://apps/%2e%2e/x");
  assert.ok(resolved === null || (resolved.startsWith("/") && !resolved.includes("..")), resolved);
});

test("page addresses join cleanly", () => {
  assert.equal(pageUrl("https://app.example.com", "/apps"), "https://app.example.com/apps");
  assert.equal(pageUrl("https://app.example.com/", "apps"), "https://app.example.com/apps");
});
