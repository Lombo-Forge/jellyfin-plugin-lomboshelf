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
