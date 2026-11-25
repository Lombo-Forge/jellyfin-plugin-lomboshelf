// Smoke tests for the web scripts, run in jsdom against a fake jellyfin-web (ApiClient) and a fake fetch.
// They prove the scripts build the DOM, fail open and respect the settings. They do NOT prove anything about a
// real jellyfin-web: its class names and behaviour are untested (see docs/design.md).
import { test, after } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const web = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "web");
const src = (f) => fs.readFileSync(path.join(web, f), "utf8");
const tick = (ms = 20) => new Promise((r) => setTimeout(r, ms));

const ep = (id, extra = {}) => ({ Id: id, Type: "Episode", SeriesName: "Some Show", SeriesId: "s1", Name: "Pilot", ParentIndexNumber: 1,
  IndexNumber: 3, RunTimeTicks: 36e9, UserData: { PlaybackPositionTicks: 24e9, PlayedPercentage: 33 }, ...extra });
const movie = (id, name, extra = {}) => ({ Id: id, Type: "Movie", Name: name, ProductionYear: 2020, RunTimeTicks: 54e9, ImageTags: { Primary: "t" }, ...extra });

function fixtures(over = {}) {
  return {
    resume: { Items: [ep("e1"), movie("m9", "Half Watched", { UserData: { PlaybackPositionTicks: 1e9, PlayedPercentage: 5 } }), { Id: "folder", Type: "Season" }] },
    nextup: { Items: [ep("e2", { SeriesId: "s2", SeriesName: "Other Show" }), ep("e3")] },
    latest: [movie("m1", "Fresh Film"), movie("m2", "Another")],
    genres: { Items: [{ Name: "Comedy" }, { Name: "Drama" }] },
    views: { Items: [{ Id: "v-movies", CollectionType: "movies" }, { Id: "v-tv", CollectionType: "tvshows" }] },
    items: (p) => ({ Items: [movie("g1", p.Genres + " One")], TotalRecordCount: 1 }),
    ...over,
  };
}

const windows = [];
// The scripts poll with setInterval, which would keep node alive: close every window when the file is done.
after(() => windows.forEach((w) => w.close()));

function page({ hash = "#/home", html = '<div id="homeTab"></div>', fx = fixtures(), cfg } = {}) {
  const dom = new JSDOM(`<!doctype html><html><body>${html}</body></html>`, { url: "http://localhost/" + hash, runScripts: "outside-only", pretendToBeVisual: true });
  const w = dom.window;
  windows.push(w);
  const calls = [];
  w.ApiClient = {
    accessToken: () => "tok", getCurrentUserId: () => "u1",
    getUrl: (p, params) => "/" + p.replace(/^\//, "") + (params ? "?" + new URLSearchParams(Object.entries(params).filter(([, v]) => v !== undefined)).toString() : ""),
    getJSON: async (url) => {
      calls.push(url);
      if (url.includes("Items/Resume")) return fx.resume;
      if (url.includes("Shows/NextUp")) return fx.nextup;
      if (url.includes("Items/Latest")) return fx.latest;
      if (url.includes("/Views") || url.includes("UserViews")) return fx.views;
      if (url.startsWith("/Genres")) return fx.genres;
      if (/Users\/u1\/Items\?/.test(url) || url.startsWith("/Items?")) return fx.items(Object.fromEntries(new URL("http://x" + url).searchParams));
      throw new Error("unexpected " + url);
    },
  };
  if (cfg) w.LomboShelfConfig = cfg;
  w.eval(src("ui.js"));
  return { dom, w, calls };
}

test("home shelf renders hero, keep watching, new this week and genre rows", async () => {
  const { w } = page();
  w.eval(src("home-shelf.js"));
  await tick();
  const root = w.document.getElementById("lshelf-shelf");
  assert.ok(root, "shelf mounted");
  assert.match(root.querySelector(".lshelf-hero h1").textContent, /^20 minutes left of Some Show, episode 3$/);
  const keep = [...root.querySelectorAll("#lshelf-keep .card")].map((c) => c.dataset.id);
  assert.deepEqual(keep, ["m9", "e2"], "resume rest then next up, no repeat of the hero's show (e3 is the same show)");
  assert.equal(root.querySelectorAll("#lshelf-new .card").length, 2);
  assert.ok(root.querySelector("#lshelf-new .lshelf-badge").textContent === "NEW");
  await tick();
  const genres = [...root.querySelectorAll(".lshelf-row.genre h2")].map((h) => h.textContent);
  assert.deepEqual(genres, ["Comedy", "Drama"]);
  assert.match(root.querySelector(".lshelf-row.genre a").getAttribute("href"), /^#\/movies\?topParentId=v-movies&lt_genre=Comedy$/);
  assert.ok(w.document.documentElement.classList.contains("lshelf-shelf-on"));
});

test("home shelf escapes titles", async () => {
  const { w } = page({ fx: fixtures({ latest: [movie("x", '<img src=x onerror="alert(1)">')] }) });
  w.eval(src("home-shelf.js"));
  await tick();
  assert.equal(w.document.querySelectorAll("#lshelf-shelf img[src=x]").length, 0);
});

test("home shelf fails open when Jellyfin does not answer", async () => {
  const { w } = page();
  w.ApiClient.getJSON = async () => { throw new Error("down"); };
  w.eval(src("home-shelf.js"));
  await tick();
  assert.equal(w.document.getElementById("lshelf-shelf"), null);
  assert.ok(!w.document.documentElement.classList.contains("lshelf-shelf-on"));
});

test("home shelf respects the row settings", async () => {
  const { w } = page({ cfg: { shelf: { resume: false, newThisWeek: false, genreLimit: 1 } } });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  const root = w.document.getElementById("lshelf-shelf");
  assert.equal(root.querySelector(".lshelf-hero"), null);
  assert.equal(root.querySelector("#lshelf-new"), null);
  assert.ok(root.querySelector("#lshelf-keep"));
  assert.equal(root.querySelectorAll(".lshelf-row.genre").length, 1);
});

test("home shelf stays away from other pages", async () => {
  const { w } = page({ hash: "#/details?id=1" });
  w.eval(src("home-shelf.js"));
  await tick();
  assert.equal(w.document.getElementById("lshelf-shelf"), null);
});

test("browse page shows genre chips and a grid, and fails open", async () => {
  const html = '<div id="moviesPage"><div><div class="itemsContainer"></div></div></div>';
  const { w } = page({ hash: "#/movies?topParentId=v-movies&lt_genre=Drama", html });
  w.eval(src("browse-genres.js"));
  await tick();
  const root = w.document.getElementById("lshelf-browse");
  assert.ok(root);
  const chips = [...root.querySelectorAll(".lshelf-chip")].map((c) => c.textContent);
  assert.deepEqual(chips, ["All", "Comedy", "Drama"]);
  assert.equal(root.querySelector('.lshelf-chip[aria-pressed="true"]').textContent, "Drama");
  assert.match(root.querySelector("#lshelf-count").textContent, /^1 movies in Drama$/);
  assert.ok(w.document.documentElement.classList.contains("lshelf-browse-on"));

  const bad = page({ hash: "#/movies", html });
  bad.w.ApiClient.getJSON = async () => { throw new Error("down"); };
  bad.w.eval(src("browse-genres.js"));
  await tick();
  assert.equal(bad.w.document.getElementById("lshelf-browse"), null);
  assert.ok(!bad.w.document.documentElement.classList.contains("lshelf-browse-on"));
});

test("header nav orders libraries and folds a group, and does nothing without settings", () => {
  const html = '<header class="MuiAppBar-root"><div style="display:flex">' +
    ["Shows", "Anime", "Movies", "Favorites", "Anime Movies", "4K Movies"].map((n) => `<a href="#/${n.toLowerCase().replace(/ /g, "")}">${n}</a>`).join("") + "</div></header>";
  const { w } = page({ html, cfg: { headerNav: { order: ["Favorites", "Movies", "Shows", "Anime"], groups: [{ label: "Anime", items: [["Anime", "Shows"], ["Anime Movies", "Movies"]] }], hide: ["4K Movies"] } } });
  w.eval(src("header-nav.js"));
  const link = (n) => [...w.document.querySelectorAll("a")].find((a) => a.textContent === n && !a.id);   // not the group button
  assert.equal(link("Favorites").style.order, "1");
  assert.equal(link("Movies").style.order, "2");
  assert.equal(link("Anime").style.display, "none");
  assert.equal(link("4K Movies").style.display, "none");
  const group = w.document.getElementById("lshelf-group-0");
  assert.ok(group);
  assert.equal(group.style.order, "4");
  group.click();
  assert.deepEqual([...w.document.querySelectorAll(".lshelf-menu a")].map((a) => a.textContent), ["Shows", "Movies"]);

  const off = page({ html });
  off.w.eval(src("header-nav.js"));
  assert.equal(off.w.document.querySelector("a").style.order, "");
});

test("loader sets safe theme variables, loads the enabled scripts and ignores unsafe values", async () => {
  const { w } = page({ cfg: { theme: { accent: "#00ff88", bg: "red; } body { display:none" }, features: { nextSeason: true, headerNav: false } } });
  w.eval(src("loader.js"));
  await tick();
  const style = w.document.documentElement.style;
  assert.equal(style.getPropertyValue("--lshelf-accent"), "#00ff88");
  assert.equal(style.getPropertyValue("--lshelf-bg"), "", "an unsafe value is dropped");
  const files = [...w.document.head.querySelectorAll("script")].map((s) => s.src.split("/").pop());
  assert.deepEqual(files, ["ui.js"], "scripts load one after another; the first is ui.js");
  assert.ok(w.document.head.querySelector('link[href$="lomboshelf.css"]'));
  assert.equal(w.LomboShelfConfig.features.homeShelf, true, "defaults are filled in");
  assert.equal(w.LomboShelfConfig.nextSeason.enabled, true);
});

test("next season prompt is off by default and asks once when enabled", async () => {
  const off = page();
  let subscribed = 0;
  off.w.ApiClient.subscribe = () => { subscribed++; };
  off.w.eval(src("next-season.js"));
  await tick();
  assert.equal(subscribed, 0);

  const on = page({ cfg: { nextSeason: { enabled: true, showArt: false } } });
  let handler;
  on.w.ApiClient.subscribe = (_t, fn) => { handler = fn; };
  const posts = [];
  const j = (o) => ({ ok: true, status: 200, json: async () => o });
  const seriesId = "s1";
  on.w.fetch = async (url, opt = {}) => {
    if (opt.method === "POST") { posts.push([url, JSON.parse(opt.body)]); return { ok: true, status: 204, json: async () => null }; }
    if (url.includes("/Users/u1/Items/ep9")) return j({ Id: "ep9", Type: "Episode", SeriesId: seriesId, SeasonId: "se1", ParentIndexNumber: 1, IndexNumber: 8 });
    if (url.includes("/Episodes")) return j({ Items: [{ IndexNumber: 7 }, { IndexNumber: 8 }] });
    if (url.includes("/Users/u1/Items/" + seriesId)) return j({ Name: "Some Show", ProviderIds: { Tmdb: "42" } });
    if (url.includes("/Seasons")) return j({ Items: [{ IndexNumber: 1, ChildCount: 8 }] });
    if (url.includes("user-status")) return j({ active: true });
    if (url.includes("/jellyseerr/tv/42")) return j({ seasons: [{ seasonNumber: 2, episodeCount: 8, airDate: "2020-01-01" }], mediaInfo: { seasons: [], requests: [] } });
    throw new Error("unexpected " + url);
  };
  on.w.eval(src("next-season.js"));
  await tick(1100);   // attach() polls once a second
  assert.ok(handler, "subscribed to UserDataChanged");
  handler({ Data: { UserId: "u1", UserDataList: [{ ItemId: "ep9", Played: true }] } });
  await tick(50);
  const dlg = on.w.document.querySelector(".lshelf-dialog");
  assert.ok(dlg, "dialog shown");
  assert.match(dlg.querySelector(".lshelf-dialog-title").textContent, /^Season 2 of Some Show is out$/);
  dlg.querySelector('[data-a="yes"]').click();
  await tick(50);
  assert.equal(posts.length, 1);
  assert.match(posts[0][0], /\/JellyfinEnhanced\/jellyseerr\/request\/tv\/42\/seasons$/);
  assert.deepEqual(posts[0][1], { mediaType: "tv", mediaId: 42, seasons: [2] });
  assert.match(on.w.document.querySelector(".lshelf-toast").textContent, /Season 2 of Some Show is on its way/);
});

test("next season prompt handles a burst of played episodes a few at a time", async () => {
  const on = page({ cfg: { nextSeason: { enabled: true, showArt: false } } });
  let handler;
  on.w.ApiClient.subscribe = (_t, fn) => { handler = fn; };
  let active = 0;
  let peak = 0;
  let total = 0;
  on.w.fetch = async () => {
    active++; total++;
    peak = Math.max(peak, active);
    await tick(15);
    active--;
    return { ok: true, status: 200, json: async () => ({ Id: "x", Type: "Movie" }) };   // not an episode: stops after one call
  };
  on.w.eval(src("next-season.js"));
  await tick(1100);
  const list = Array.from({ length: 12 }, (_, i) => ({ ItemId: "ep" + i, Played: true }));
  handler({ Data: { UserId: "u1", UserDataList: list } });
  await tick(300);
  assert.equal(total, 12, "every item is still handled");
  assert.ok(peak <= 2, "at most 2 requests at once, saw " + peak);
});

test("loader adds the plugin version to the files it loads", async () => {
  const { w } = page({ cfg: { features: { theme: true, homeShelf: false, browse: false, nextSeason: false, headerNav: false } } });
  Object.defineProperty(w.document, "currentScript", { value: { src: "http://localhost/LomboShelf/web/loader.js?v=1.2.3.0" } });
  w.eval(src("loader.js"));
  const hrefs = Array.from(w.document.head.querySelectorAll("link,script")).map((e) => e.href || e.src);
  assert.ok(hrefs.some((h) => h.endsWith("lomboshelf.css?v=1.2.3.0")), hrefs.join());
  await tick();
  assert.ok(Array.from(w.document.head.querySelectorAll("script")).some((e) => e.src.endsWith("ui.js?v=1.2.3.0")));
});

// ---- collection rows (off by default) ----
const box = (id, name) => ({ Id: id, Type: "BoxSet", Name: name });
// Answers the BoxSet search by NameStartsWith (like Jellyfin, a looser match than the exact name) and the children by ParentId.
function collFx(boxes, children, over = {}) {
  return fixtures({
    items: (p) => {
      if (p.IncludeItemTypes === "BoxSet") return { Items: boxes.filter((b) => b.Name.toLowerCase().startsWith((p.NameStartsWith || "").toLowerCase())) };
      if (p.ParentId) return { Items: (children[p.ParentId] || []).slice(0, Number(p.Limit)) };
      return { Items: [movie("g1", p.Genres + " One")], TotalRecordCount: 1 };
    },
    ...over,
  });
}
const many = (n) => Array.from({ length: n }, (_, i) => movie("c" + i, "Film " + i));
const rowTitles = (w) => [...w.document.querySelectorAll("#lshelf-shelf .lshelf-row.collection h2")].map((h) => h.textContent);

test("collection rows: nothing is asked or shown by default", async () => {
  const { w, calls } = page({ fx: collFx([box("b1", "Leaving soon")], { b1: many(3) }) });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.equal(w.document.querySelector(".lshelf-row.collection"), null);
  assert.equal(calls.filter((u) => u.includes("BoxSet")).length, 0);
});

test("collection rows: a named collection renders as a row after New this week, before genres", async () => {
  const { w, calls } = page({ cfg: { collectionRows: [{ name: "Leaving soon" }, { name: "Staff picks", title: "Picks", limit: 2 }] },
    fx: collFx([box("b1", "Leaving soon"), box("b2", "Staff picks")], { b1: many(3), b2: many(5) }) });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.deepEqual(rowTitles(w), ["Leaving soon", "Picks"]);
  const rows = [...w.document.querySelectorAll("#lshelf-shelf .lshelf-row.collection")];
  assert.equal(rows[0].querySelectorAll(".card").length, 3);
  assert.equal(rows[1].querySelectorAll(".card").length, 2, "limit 2");
  const order = [...w.document.querySelectorAll("#lshelf-shelf > section")].map((s) => s.id || s.className);
  const at = (name) => order.findIndex((c) => c.includes(name));
  assert.ok(at("lshelf-new") < at("collection") && at("collection") < at("genre"), order.join());
  assert.ok(calls.some((u) => u.includes("ParentId=b1")), "children are asked by the collection's id");
});

test("collection rows: the name must match exactly (a longer name is not it) and a missing collection shows nothing", async () => {
  const { w } = page({ cfg: { collectionRows: [{ name: "Leaving" }, { name: "Nope" }] },
    fx: collFx([box("b1", "Leaving soon")], { b1: many(3) }) });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.deepEqual(rowTitles(w), []);
  assert.equal(w.document.querySelectorAll(".lshelf-collslot").length, 0, "empty slots are removed");
  assert.ok(w.document.querySelector("#lshelf-keep"), "the rest of the shelf is untouched");
  assert.ok(w.document.documentElement.classList.contains("lshelf-shelf-on"));
});

test("collection rows: an empty collection shows nothing", async () => {
  const { w } = page({ cfg: { collectionRows: [{ name: "Leaving soon" }] }, fx: collFx([box("b1", "Leaving soon")], { b1: [] }) });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.deepEqual(rowTitles(w), []);
  assert.equal(w.document.querySelectorAll(".lshelf-collslot").length, 0);
});

test("collection rows: an API error fails open for that row only", async () => {
  const fx = collFx([box("b1", "Leaving soon")], { b1: many(2) });
  const good = fx.items;
  fx.items = (p) => { if (p.IncludeItemTypes === "BoxSet") throw new Error("boom"); return good(p); };
  const { w } = page({ cfg: { collectionRows: [{ name: "Leaving soon" }] }, fx });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.deepEqual(rowTitles(w), []);
  assert.equal(w.document.querySelectorAll(".lshelf-collslot").length, 0);
  assert.ok(w.document.querySelector("#lshelf-new"), "other rows still render");
  assert.ok(w.document.documentElement.classList.contains("lshelf-shelf-on"));
});

test("collection rows: names, titles and item titles are escaped", async () => {
  const evil = '<img src=x onerror="alert(1)">';
  const { w } = page({ cfg: { collectionRows: [{ name: evil, title: '<b id="pwn">T</b>' }] },
    fx: collFx([box("b1", evil)], { b1: [movie("x", evil)] }) });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.equal(w.document.querySelectorAll("#lshelf-shelf img[src=x]").length, 0);
  assert.equal(w.document.getElementById("pwn"), null);
  assert.equal(rowTitles(w)[0], '<b id="pwn">T</b>', "shown as text");
});

test("collection rows: at most 5 rows, limit 1-50, bad entries ignored", async () => {
  const names = ["A", "B", "C", "D", "E", "F", "G"];
  const children = {};
  names.forEach((n) => { children["id" + n] = many(60); });
  const lim = { A: 500, B: -3, C: 7 };
  const cfg = { collectionRows: [...names.map((n) => ({ name: n, limit: lim[n] })), null, { name: "  " }, { title: "x" }] };
  const { w } = page({ cfg, fx: collFx(names.map((n) => box("id" + n, n)), children) });
  w.eval(src("home-shelf.js"));
  await tick(); await tick();
  assert.deepEqual(rowTitles(w), ["A", "B", "C", "D", "E"]);
  const count = (i) => w.document.querySelectorAll("#lshelf-shelf .lshelf-row.collection")[i].querySelectorAll(".card").length;
  assert.equal(count(0), 50, "limit above 50 is capped");
  assert.equal(count(1), 16, "limit below 1 takes the default");
  assert.equal(count(2), 7);
  assert.equal(count(3), 16, "no limit takes the default");
});

test("collection rows: loader defaults to an empty list and keeps a given list", () => {
  const a = page();
  a.w.eval(src("loader.js"));
  // JSON.stringify: arrays made inside jsdom have another prototype than the ones made here
  assert.equal(JSON.stringify(a.w.LomboShelfConfig.collectionRows), "[]");
  const b = page({ cfg: { collectionRows: [{ name: "X" }] } });
  b.w.eval(src("loader.js"));
  assert.equal(JSON.stringify(b.w.LomboShelfConfig.collectionRows), '[{"name":"X"}]');
});

// ---- storage tag and keep button (off by default; they call the plugin's own routes) ----
const ID = "0123456789abcdef0123456789abcdef";
// As jellyfin-web 12.4 builds it: an earlier details page stays in the document, hidden, ahead of the one now shown.
const detailsDiv = (cls) => `<div class="page itemDetailPage ${cls}"><div class="nameContainer"><h1 class="itemName">A Film</h1></div>` +
  '<div class="detailPagePrimaryContent"><div class="detailSection"><p class="overview">About it.</p></div></div></div>';
const detailsHtml = detailsDiv("hide") + detailsDiv("");

// A fake fetch that answers by "METHOD path" and records the calls. `routes[key]` is { status, body } or a function.
function withFetch(w, routes) {
  const calls = [];
  w.fetch = async (url, opts = {}) => {
    const key = (opts.method || "GET") + " " + url;
    calls.push({ key, opts });
    const r = typeof routes[key] === "function" ? routes[key](opts) : routes[key];
    if (!r) throw new Error("unexpected " + key);
    if (r.reject) throw new Error("network");
    return { status: r.status, ok: r.status < 300, json: async () => { if (r.body === undefined) throw new Error("no body"); return r.body; } };
  };
  return calls;
}
const detailsPage = (cfg, extra = {}) => page({ hash: "#/details?id=" + ID, html: detailsHtml, cfg, ...extra });

test("storage tag and keep button do nothing by default", async () => {
  const { w } = detailsPage({});
  const calls = withFetch(w, {});
  w.eval(src("storage-tag.js"));
  w.eval(src("keep-button.js"));
  await tick();
  assert.equal(calls.length, 0);
  assert.equal(w.document.querySelector(".lshelf-storage-tag, .lshelf-keep-box"), null);
});

test("storage tag and keep note go to the top of the details text on the page now shown", async () => {
  const { w } = detailsPage({ features: { storageTags: true, keep: true } });
  withFetch(w, {
    ["GET /LomboShelf/storage/" + ID]: { status: 200, body: { tier: "ssd" } },
    ["GET /LomboShelf/leaving/" + ID]: { status: 200, body: { days_left: 3, can_keep: true } },
  });
  w.eval(src("storage-tag.js"));
  w.eval(src("keep-button.js"));
  await tick(80);
  for (const sel of ["#lshelf-storage-tag", "#lshelf-keep-box"]) {
    const el = w.document.querySelector(sel);
    assert.ok(el.parentNode.matches(".itemDetailPage:not(.hide) .detailSection"), sel);
  }
  assert.equal(w.document.querySelector("#lshelf-keep-box").nextSibling.id, "lshelf-storage-tag", "note first, then badge");
});

test("loader loads the two scripts only when their switch is on", async () => {
  const a = page({ cfg: { features: { homeShelf: false, browse: false } } });
  a.w.eval(src("loader.js"));
  assert.equal(a.w.LomboShelfConfig.features.storageTags, false);
  assert.equal(a.w.LomboShelfConfig.features.keep, false);
  const b = page({ cfg: { features: { homeShelf: false, browse: false, storageTags: true, keep: true } } });
  b.w.eval(src("loader.js"));
  assert.equal(b.w.LomboShelfConfig.features.storageTags, true);
});

test("storage tag shows SSD once per page view and asks only the plugin route", async () => {
  const { w } = detailsPage({ features: { storageTags: true } });
  const calls = withFetch(w, { ["GET /LomboShelf/storage/" + ID]: { status: 200, body: { tier: "ssd" } } });
  w.eval(src("storage-tag.js"));
  await tick(80);
  const tag = w.document.querySelector("#lshelf-storage-tag");
  assert.equal(tag.textContent, "SSD");
  assert.ok(tag.classList.contains("ssd"));
  assert.equal(calls.length, 1);
  assert.equal(calls[0].opts.headers.Authorization, 'MediaBrowser Token="tok"');
  w.dispatchEvent(new w.Event("hashchange"));   // same id, rebuilt page: re-asks (new page view) but never twice for one view
  await tick(120);
  assert.equal(w.document.querySelectorAll("#lshelf-storage-tag").length, 1);
});

test("storage tag shows HDD, and nothing on 204, an error, junk or a non-details page", async () => {
  const hdd = detailsPage({ features: { storageTags: true } });
  withFetch(hdd.w, { ["GET /LomboShelf/storage/" + ID]: { status: 200, body: { tier: "hdd" } } });
  hdd.w.eval(src("storage-tag.js"));
  await tick(80);
  assert.equal(hdd.w.document.querySelector("#lshelf-storage-tag").textContent, "HDD");
  for (const r of [{ status: 204 }, { status: 500 }, { reject: true }, { status: 200, body: { tier: "<img src=x>" } }, { status: 200, body: { tier: "tape" } }, { status: 200 }]) {
    const p = detailsPage({ features: { storageTags: true } });
    withFetch(p.w, { ["GET /LomboShelf/storage/" + ID]: r });
    p.w.eval(src("storage-tag.js"));
    await tick(80);
    assert.equal(p.w.document.querySelector("#lshelf-storage-tag"), null, JSON.stringify(r));
    assert.equal(p.w.document.querySelector("img"), null);
  }
  const home = page({ cfg: { features: { storageTags: true } } });
  const calls = withFetch(home.w, {});
  home.w.eval(src("storage-tag.js"));
  await tick(80);
  assert.equal(calls.length, 0);
});

const leaving = (extra = {}) => ({ ["GET /LomboShelf/leaving/" + ID]: { status: 200, body: { days_left: 3, can_keep: true } }, ...extra });

test("keep: shows the note and button, posts the id, shows the message and re-checks", async () => {
  const { w } = detailsPage({ features: { keep: true } });
  let left = { days_left: 3, can_keep: true };
  const calls = withFetch(w, {
    ["GET /LomboShelf/leaving/" + ID]: () => ({ status: 200, body: left }),
    "POST /LomboShelf/keep": () => { left = { days_left: 30, can_keep: false }; return { status: 200, body: { ok: true, message: "Kept for 30 more days" } }; },
  });
  w.eval(src("keep-button.js"));
  await tick(80);
  assert.equal(w.document.querySelector(".lshelf-keep-note").textContent, "Leaving in 3 days");
  const btn = w.document.querySelector(".lshelf-keep-btn");
  assert.equal(btn.textContent, "Keep it longer");
  btn.click();
  assert.ok(btn.disabled, "disabled while waiting");
  await tick(80);
  const post = calls.find((c) => c.key.startsWith("POST"));
  assert.equal(post.opts.body, JSON.stringify({ itemId: ID }));
  assert.equal(post.opts.headers["Content-Type"], "application/json");
  assert.equal(calls.filter((c) => c.key.startsWith("GET")).length, 2, "checked again afterwards");
  assert.equal(w.document.querySelector(".lshelf-keep-msg").textContent, "Kept for 30 more days");
  assert.equal(w.document.querySelector(".lshelf-keep-note").textContent, "Leaving in 30 days");
  assert.equal(w.document.querySelector(".lshelf-keep-btn"), null, "no button once it cannot be kept");
});

test("keep: wording for no date, one day, no button without can_keep, nothing on 204", async () => {
  const cases = [[{ days_left: null, can_keep: false }, "Leaving soon", false], [{ days_left: 1, can_keep: true }, "Leaving in 1 day", true], [{ days_left: 0, can_keep: false }, "Leaving today", false]];
  for (const [body, text, button] of cases) {
    const p = detailsPage({ features: { keep: true } });
    withFetch(p.w, leaving({ ["GET /LomboShelf/leaving/" + ID]: { status: 200, body } }));
    p.w.eval(src("keep-button.js"));
    await tick(80);
    assert.equal(p.w.document.querySelector(".lshelf-keep-note").textContent, text);
    assert.equal(!!p.w.document.querySelector(".lshelf-keep-btn"), button);
  }
  for (const r of [{ status: 204 }, { status: 404 }, { reject: true }]) {
    const p = detailsPage({ features: { keep: true } });
    withFetch(p.w, { ["GET /LomboShelf/leaving/" + ID]: r });
    p.w.eval(src("keep-button.js"));
    await tick(80);
    assert.equal(p.w.document.querySelector(".lshelf-keep-box"), null);
  }
});

test("keep: refusal text is shown as text, a server error shows only a generic line", async () => {
  const refuse = detailsPage({ features: { keep: true } });
  withFetch(refuse.w, leaving({ "POST /LomboShelf/keep": { status: 422, body: { ok: false, message: '<img src=x onerror="alert(1)"> already kept' } } }));
  refuse.w.eval(src("keep-button.js"));
  await tick(80);
  refuse.w.document.querySelector(".lshelf-keep-btn").click();
  await tick(80);
  assert.equal(refuse.w.document.querySelector(".lshelf-keep-msg").textContent, '<img src=x onerror="alert(1)"> already kept');
  assert.equal(refuse.w.document.querySelector("img"), null, "set as text, never as HTML");

  for (const r of [{ status: 502, body: { ok: false, message: "could not complete the request" } }, { status: 500, body: { message: "Traceback: secret" } }, { reject: true }, { status: 200, body: {} }]) {
    const p = detailsPage({ features: { keep: true } });
    withFetch(p.w, leaving({ "POST /LomboShelf/keep": r }));
    p.w.eval(src("keep-button.js"));
    await tick(80);
    p.w.document.querySelector(".lshelf-keep-btn").click();
    await tick(80);
    const msg = p.w.document.querySelector(".lshelf-keep-msg").textContent;
    assert.ok(!/Traceback|secret/.test(msg), msg);
    assert.match(msg, /could not complete the request/i);
  }
});

test("keep: a long message is cut to 200 characters; a rate-limit answer shows its message", async () => {
  const p = detailsPage({ features: { keep: true } });
  withFetch(p.w, leaving({ "POST /LomboShelf/keep": { status: 429, body: { ok: false, message: "x".repeat(500) } } }));
  p.w.eval(src("keep-button.js"));
  await tick(80);
  p.w.document.querySelector(".lshelf-keep-btn").click();
  await tick(80);
  assert.equal(p.w.document.querySelector(".lshelf-keep-msg").textContent.length, 200);
});

// ---- travel card (off by default; calls the plugin's own travel route) ----
const travelInfo = (over = {}) => ({ active: null, next_allowed: null, max_days: 14, ...over });
const travelCfg = { features: { travel: true, homeShelf: false, browse: false } };
const getTravel = (body = travelInfo(), status = 200) => ({ "GET /LomboShelf/travel": { status, body } });

test("travel: does nothing by default and the loader defaults the switch to false", async () => {
  const off = page({ cfg: {} });
  const calls = withFetch(off.w, {});
  off.w.eval(src("travel-button.js"));
  await tick(60);
  assert.equal(calls.length, 0);
  assert.equal(off.w.document.querySelector("#lshelf-travel"), null);
  const a = page({ cfg: { features: { homeShelf: false, browse: false } } });
  a.w.eval(src("loader.js"));
  assert.equal(a.w.LomboShelfConfig.features.travel, false);
});

test("travel: a collapsed card on Home with a days picker and the button", async () => {
  const { w } = page({ cfg: travelCfg });
  const calls = withFetch(w, getTravel());
  w.eval(src("travel-button.js"));
  await tick(80);
  const card = w.document.querySelector("#homeTab > details.lshelf-travel");
  assert.ok(card);
  assert.equal(card.open, false, "collapsed");
  assert.equal(card.querySelector("summary").textContent, "Travelling?");
  assert.equal(card.querySelector(".lshelf-travel-status").textContent, "No window is open.");
  const days = card.querySelector(".lshelf-travel-days");
  assert.equal(days.min, "1");
  assert.equal(days.max, "14");
  assert.equal(card.querySelector(".lshelf-travel-btn").textContent, "Allow sign-in from abroad");
  assert.equal(calls.length, 1);
  assert.equal(calls[0].opts.headers.Authorization, 'MediaBrowser Token="tok"');
});

test("travel: shows the end of an open window, or when the next one is allowed", async () => {
  const a = page({ cfg: travelCfg });
  withFetch(a.w, getTravel(travelInfo({ active: { ends: 1900000000 } })));
  a.w.eval(src("travel-button.js"));
  await tick(80);
  assert.match(a.w.document.querySelector(".lshelf-travel-status").textContent, /^Sign-in from abroad is allowed until .+\.$/);
  const b = page({ cfg: travelCfg });
  withFetch(b.w, getTravel(travelInfo({ next_allowed: 1900000500 })));
  b.w.eval(src("travel-button.js"));
  await tick(80);
  assert.match(b.w.document.querySelector(".lshelf-travel-status").textContent, /^You can open another window after .+\.$/);
});

test("travel: posts only the days, disables while waiting, shows the message and asks again", async () => {
  const { w } = page({ cfg: travelCfg });
  let release;
  let opened = false;
  const calls = withFetch(w, { "GET /LomboShelf/travel": () => ({ status: 200, body: opened ? travelInfo({ active: { ends: 1900000000 } }) : travelInfo() }) });
  const base = w.fetch;
  w.fetch = async (url, opts = {}) => {   // hold the POST until the test lets it go
    if (opts.method !== "POST") return base(url, opts);
    calls.push({ key: "POST " + url, opts });
    await new Promise((r) => { release = r; });
    opened = true;
    return { status: 200, ok: true, json: async () => ({ ok: true, message: "Window opened for 5 days" }) };
  };
  w.eval(src("travel-button.js"));
  await tick(80);
  const input = w.document.querySelector(".lshelf-travel-days");
  input.value = "5";
  input.dispatchEvent(new w.Event("input"));
  w.document.querySelector(".lshelf-travel-btn").click();
  await tick(40);
  assert.equal(w.document.querySelector(".lshelf-travel-btn").disabled, true, "disabled while waiting");
  const post = calls.find((c) => c.key.startsWith("POST"));
  assert.deepEqual(JSON.parse(post.opts.body), { days: 5 }, "only the days, no user id");
  assert.equal(post.opts.headers.Authorization, 'MediaBrowser Token="tok"');
  release();
  await tick(80);
  assert.equal(w.document.querySelector(".lshelf-travel-msg").textContent, "Window opened for 5 days");
  assert.match(w.document.querySelector(".lshelf-travel-status").textContent, /allowed until/);
  assert.equal(w.document.querySelector(".lshelf-travel-btn").disabled, false);
});

test("travel: a refusal is shown as text; a server error shows only a generic line", async () => {
  const refuse = page({ cfg: travelCfg });
  withFetch(refuse.w, { ...getTravel(), "POST /LomboShelf/travel": { status: 422, body: { ok: false, message: '<img src=x onerror="alert(1)"> use your admin tools' } } });
  refuse.w.eval(src("travel-button.js"));
  await tick(80);
  refuse.w.document.querySelector(".lshelf-travel-btn").click();
  await tick(80);
  assert.equal(refuse.w.document.querySelector(".lshelf-travel-msg").textContent, '<img src=x onerror="alert(1)"> use your admin tools');
  assert.equal(refuse.w.document.querySelector("#lshelf-travel img"), null);
  for (const r of [{ status: 502, body: { ok: false, message: "http://visa.internal:8097 secret" } }, { status: 500, body: { message: "stack" } }, { reject: true }, { status: 200 }]) {
    const p = page({ cfg: travelCfg });
    withFetch(p.w, { ...getTravel(), "POST /LomboShelf/travel": r });
    p.w.eval(src("travel-button.js"));
    await tick(80);
    p.w.document.querySelector(".lshelf-travel-btn").click();
    await tick(80);
    assert.equal(p.w.document.querySelector(".lshelf-travel-msg").textContent, "Could not complete the request.", JSON.stringify(r));
  }
});

test("travel: a long message is cut to 200; days outside 1..max are not sent", async () => {
  const p = page({ cfg: travelCfg });
  const calls = withFetch(p.w, { ...getTravel(), "POST /LomboShelf/travel": { status: 429, body: { ok: false, message: "x".repeat(500) } } });
  p.w.eval(src("travel-button.js"));
  await tick(80);
  for (const bad of ["0", "15", "", "abc"]) {
    p.w.document.querySelector(".lshelf-travel-days").value = bad;
    p.w.document.querySelector(".lshelf-travel-btn").click();
    await tick(30);
    assert.equal(calls.filter((c) => c.key.startsWith("POST")).length, 0, "not sent for " + bad);
    assert.equal(p.w.document.querySelector(".lshelf-travel-msg").textContent, "Pick a number of days from 1 to 14.");
  }
  p.w.document.querySelector(".lshelf-travel-days").value = "3";
  p.w.document.querySelector(".lshelf-travel-btn").click();
  await tick(80);
  assert.equal(p.w.document.querySelector(".lshelf-travel-msg").textContent.length, 200);
});

test("travel: no card on other pages, on errors or on a junk answer, and no call storm after a failure", async () => {
  const other = page({ hash: "#/details?id=" + ID, html: detailsHtml + '<div id="homeTab"></div>', cfg: travelCfg });
  const c0 = withFetch(other.w, getTravel());
  other.w.eval(src("travel-button.js"));
  await tick(80);
  assert.equal(c0.length, 0);
  const junk = [{ status: 404 }, { status: 502, body: { ok: false } }, { reject: true }, { status: 200, body: {} },
    { status: 200, body: { active: null, next_allowed: null, max_days: 0 } }, { status: 200, body: { active: null, next_allowed: null, max_days: "7" } }];
  for (const r of junk) {
    const p = page({ cfg: travelCfg });
    const calls = withFetch(p.w, { "GET /LomboShelf/travel": r });
    p.w.eval(src("travel-button.js"));
    await tick(80);
    assert.equal(p.w.document.querySelector("#lshelf-travel"), null, JSON.stringify(r));
    await tick(1700);   // the page polls every 1.5 s: after a failure it must wait, not ask again
    assert.equal(calls.length, 1, "no retry within the back-off: " + JSON.stringify(r));
  }
});
