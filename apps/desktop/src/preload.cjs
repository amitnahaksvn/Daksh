"use strict";
// The only thing the web page can see of the desktop app: what it is running on. No Node, no files, no shell.
const { contextBridge } = require("electron");

contextBridge.exposeInMainWorld("dakshaDesktop", Object.freeze({
  isDesktop: true,
  platform: process.platform,
  arch: process.arch,
}));
