"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { PAGES, buildMenuTemplate } = require("../src/menu.cjs");

const make = (over = {}) => {
  const calls = [];
  const template = buildMenuTemplate({ isMac: false, isDev: false, go: (p, o) => calls.push([p, o]), openExternal: (u) => calls.push(["ext", u]), appName: "Daksha", appUrl: "https://app.example.com", ...over });
  return { template, calls };
};
const flat = (items) => items.flatMap((i) => [i, ...(i.submenu ? flat(i.submenu) : [])]);

test("every page of the app is reachable from the View menu with a shortcut", () => {
  const { template, calls } = make();
  const items = flat(template);
  for (const [label, path, key] of PAGES) {
    const item = items.find((i) => i.label === label && i.accelerator === `CmdOrCtrl+${key}`);
    assert.ok(item, label);
    item.click();
    assert.deepEqual(calls.at(-1), [path, undefined]);
  }
});

test("developer tools are only in development builds", () => {
  assert.equal(flat(make({ isDev: false }).template).some((i) => i.role === "toggleDevTools"), false);
  assert.equal(flat(make({ isDev: true }).template).some((i) => i.role === "toggleDevTools"), true);
});

test("macOS gets an application menu, Windows a quit item under File", () => {
  assert.equal(make({ isMac: true }).template[0].label, "Daksha");
  assert.ok(flat(make({ isMac: false }).template).some((i) => i.role === "quit"));
});

test("the help link opens the web app in the browser", () => {
  const { template, calls } = make();
  flat(template).find((i) => i.label === "Daksha on the web").click();
  assert.deepEqual(calls.at(-1), ["ext", "https://app.example.com"]);
});
