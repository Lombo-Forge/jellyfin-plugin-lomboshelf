/*
 * LomboShelf loader: the one script the plugin adds to jellyfin-web's index.html. It reads
 * window.LomboShelfConfig (the plugin writes it just before this script; see docs/design.md), fills in defaults,
 * sets the theme colours as CSS variables, then loads the stylesheet and the feature scripts that are switched on,
 * in order (ui.js first). Nothing here talks to the network except loading those files from the same plugin route.
 */
(function () {
  "use strict";
  if (window.__lomboShelfLoader) return;
  window.__lomboShelfLoader = true;

  var script = document.currentScript;
  var base = script && script.src ? script.src.replace(/[^/]*$/, "") : "/LomboShelf/web/";
  // Same ?v=<plugin version> as the loader's own tag, so every file is refetched after an update.
  var vq = script && script.src ? (script.src.match(/\?v=[A-Za-z0-9._-]+/) || [""])[0] : "";
  var defaults = {
    theme: { bg: "#141414", surface: "#1f1f1f", surface2: "#2a2a2a", text: "#e5e5e5", muted: "#a3a3a3", accent: "#e50914", accentHover: "#f6121d", radius: "6px" },
    features: { theme: true, homeShelf: true, browse: true, nextSeason: false, headerNav: false },
    shelf: { resume: true, keepWatching: true, newThisWeek: true, genres: true, genreLimit: 0 },
    collectionRows: [],
    browse: { mergeLibraries: [] },
    nextSeason: { showArt: true },
    headerNav: null
  };

  function merge(a, b) {
    var out = {};
    Object.keys(a).forEach(function (k) { out[k] = a[k]; });
    Object.keys(b || {}).forEach(function (k) {
      out[k] = (a[k] && typeof a[k] === "object" && !Array.isArray(a[k]) && b[k] && typeof b[k] === "object") ? merge(a[k], b[k]) : b[k];
    });
    return out;
  }

  var conf = merge(defaults, window.LomboShelfConfig);
  conf.nextSeason.enabled = !!conf.features.nextSeason;   // features.nextSeason is the one switch
  window.LomboShelfConfig = conf;

  // Theme: only colours are variables here; the stylesheet does the rest. Colour values are validated so a
  // setting cannot inject CSS.
  var SAFE = /^(#[0-9a-fA-F]{3,8}|rgba?\([0-9., %]+\)|[0-9.]+(px|em|rem))$/;
  var VARS = { bg: "--lshelf-bg", surface: "--lshelf-surface", surface2: "--lshelf-surface-2", text: "--lshelf-text", muted: "--lshelf-muted",
    accent: "--lshelf-accent", accentHover: "--lshelf-accent-hover", radius: "--lshelf-radius" };
  Object.keys(VARS).forEach(function (k) {
    var v = String(conf.theme[k] == null ? "" : conf.theme[k]).trim();
    if (SAFE.test(v)) document.documentElement.style.setProperty(VARS[k], v);
  });

  function css(file) {
    var l = document.createElement("link");
    l.rel = "stylesheet"; l.href = base + file + vq;
    document.head.appendChild(l);
  }
  function js(file) {
    return new Promise(function (resolve) {
      var s = document.createElement("script");
      s.src = base + file + vq; s.async = false;
      s.onload = s.onerror = function () { resolve(); };   // one missing file never blocks the others
      document.head.appendChild(s);
    });
  }

  css("lomboshelf.css");
  var f = conf.features;
  var files = ["ui.js"];
  if (f.homeShelf) files.push("home-shelf.js");
  if (f.browse) files.push("browse-genres.js");
  if (f.nextSeason) files.push("next-season.js");
  if (f.headerNav && conf.headerNav) files.push("header-nav.js");
  files.reduce(function (p, file) { return p.then(function () { return js(file); }); }, Promise.resolve());
  if (!f.theme) document.documentElement.classList.add("lshelf-no-theme");
})();
