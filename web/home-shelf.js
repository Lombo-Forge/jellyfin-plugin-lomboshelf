/*
 * LomboShelf: the shelf Home. Adds, above Jellyfin's own Home rows,
 *   a Resume card, Keep watching, New this week, and one row per genre.
 * Everything else on Home (the Favorites tab, the page itself) stays Jellyfin's.
 *
 * Fail-open: Jellyfin's own rows are hidden only (class lshelf-shelf-on on <html>) after the shelf has
 * rendered. Any error removes the shelf and the stock rows come back. The stock rows stay in the page (hidden)
 * because Resume and Keep watching press play through the matching stock card (see ui.play).
 *
 * Needs ui.js. Works in the browser and the phone apps that embed jellyfin-web; Jellyfin's native apps ignore
 * injected scripts.
 */
(function () {
  "use strict";
  if (window.__lomboShelf) return;
  window.__lomboShelf = true;

  var busy = false;
  var failedAt = 0;                  // when the last build failed; mount() waits RETRY_MS before trying again
  var RETRY_MS = 5 * 60 * 1000;
  var HTML = document.documentElement;

  function settings() {
    var c = (window.__lomboShelfUI && window.__lomboShelfUI.cfg().shelf) || {};
    return {
      resume: c.resume !== false, keepWatching: c.keepWatching !== false, newThisWeek: c.newThisWeek !== false,
      genres: c.genres !== false, genreLimit: Number(c.genreLimit) > 0 ? Number(c.genreLimit) : 0
    };
  }

  function onHome() { return /^#\/home(\?|$)/.test(location.hash) && !/tab=1/.test(location.hash); }
  function viewIds(views) {
    var out = { movies: null, tvshows: null };
    (views.Items || []).forEach(function (v) {
      if (v.CollectionType === "movies") out.movies = v.Id;
      if (v.CollectionType === "tvshows") out.tvshows = v.Id;
    });
    return out;
  }

  async function load(U) {
    var uid = U.uid();
    var f = U.FIELDS + ",UserData,SeriesName,ParentIndexNumber,IndexNumber";
    var settle = function (p) { return p.then(function (v) { return v; }, function () { return null; }); };
    var res = await Promise.all([
      settle(U.jf("Users/" + uid + "/Items/Resume", { Limit: 12, MediaTypes: "Video", Fields: f, EnableTotalRecordCount: false })),
      settle(U.jf("Shows/NextUp", { userId: uid, Limit: 8, Fields: f, EnableTotalRecordCount: false })),
      settle(U.jf("Users/" + uid + "/Items/Latest", { Limit: 16, IncludeItemTypes: "Movie,Series", Fields: f })),
      settle(U.jf("Genres", { userId: uid, IncludeItemTypes: "Movie,Series", Recursive: true, SortBy: "SortName" })),
      settle(U.jf("Users/" + uid + "/Views"))
    ]);
    // Jellyfin itself not answering (Resume, Latest and Genres all failed): fail open, show its own rows.
    if (!res[0] && !res[2] && !res[3]) throw new Error("Jellyfin did not answer");
    return {
      // Jellyfin's Resume list also returns the Season and Series folders of a half-watched show; only playable ones count.
      resume: ((res[0] && res[0].Items) || []).filter(function (i) { return i.Type === "Movie" || i.Type === "Episode"; }),
      nextup: (res[1] && res[1].Items) || [],
      latest: Array.isArray(res[2]) ? res[2] : ((res[2] && res[2].Items) || []),
      genres: ((res[3] && res[3].Items) || []).map(function (g) { return g.Name; }),
      views: viewIds(res[4] || {})
    };
  }

  // "38 minutes", "1 hour 12 minutes", "2 hours", "Less than a minute".
  function leftPhrase(ticks) {
    var m = Math.round(ticks / 6e8);
    if (ticks <= 0) return "";
    if (m < 1) return "Less than a minute";
    if (m < 90) return m + (m === 1 ? " minute" : " minutes");
    var h = Math.floor(m / 60), r = m % 60;
    return h + (h === 1 ? " hour" : " hours") + (r ? " " + r + (r === 1 ? " minute" : " minutes") : "");
  }

  // The headline is the fact: "38 minutes left of Some Show, episode 4" / "38 minutes left of Some Film".
  // No runtime known: just the title.
  function heroHeadline(it) {
    var title = it.SeriesName || it.Name || "";
    var ud = it.UserData || {};
    var left = it.RunTimeTicks ? leftPhrase(it.RunTimeTicks - (ud.PlaybackPositionTicks || 0)) : "";
    if (!left) return title;
    return left + " left of " + title + (it.Type === "Episode" && it.IndexNumber ? ", episode " + it.IndexNumber : "");
  }

  function hero(U, it) {
    var title = it.SeriesName || it.Name || "";
    var pct = (it.UserData || {}).PlayedPercentage || 0;
    // under the headline: where it is (season and episode name), or the year for a movie
    var line = it.Type === "Episode" ? ["Season " + (it.ParentIndexNumber || "?"), it.Name].filter(Boolean).join(" · ") : (it.ProductionYear || "");
    var art = U.still(it);
    return '<section class="lshelf-hero" aria-label="Resume">' +
      '<a class="lshelf-hero-still focusable" data-play="' + U.esc(it.Id) + '" href="' + U.href(it.Id) + '" aria-label="Resume ' + U.esc(title) + '">' +
      (art ? '<img alt="" src="' + U.esc(art) + '">' : "") +
      '<span class="lshelf-playbtn" aria-hidden="true">▶</span>' +
      (pct > 0 ? '<span class="lshelf-prog"><i style="width:' + Math.min(100, Math.round(pct)) + '%"></i></span>' : "") + "</a>" +
      '<div class="lshelf-hero-body">' +
      "<h1>" + U.esc(heroHeadline(it)) + "</h1>" + (line ? "<p>" + U.esc(line) + "</p>" : "") +
      '<div class="lshelf-btns"><a class="lshelf-btn accent focusable" data-play="' + U.esc(it.Id) + '" href="' + U.href(it.Id) + '">▶ Resume</a>' +
      '<a class="lshelf-btn focusable" href="' + U.href(it.SeriesId || it.Id) + '">Details</a></div></div></section>';
  }

  function render(U, d, root) {
    var s = settings();
    var parts = [];
    var first = s.resume ? d.resume[0] : null;
    if (first) parts.push(hero(U, first));
    // Keep watching: the rest of the resume list, then Next Up (one tile per show), no repeats.
    if (s.keepWatching) {
      var seen = {};
      if (first) { seen[first.Id] = 1; if (first.SeriesId) seen[first.SeriesId] = 1; }
      var keep = [];
      (first ? d.resume.slice(1) : d.resume).concat(d.nextup).forEach(function (it) {
        if (seen[it.Id] || (it.SeriesId && seen[it.SeriesId])) return;
        seen[it.Id] = 1; if (it.SeriesId) seen[it.SeriesId] = 1;
        keep.push(it);
      });
      if (keep.length) parts.push(U.row("Keep watching", keep.slice(0, 8).map(U.wide).join(""), { cls: "wide", id: "lshelf-keep" }));
    }
    if (s.newThisWeek && d.latest.length) {
      parts.push(U.row("New this week", d.latest.map(function (it) { return U.card(it, { badge: "NEW", cls: "new" }); }).join(""), { id: "lshelf-new" }));
    }
    if (s.genres) {
      var names = s.genreLimit ? d.genres.slice(0, s.genreLimit) : d.genres;
      names.forEach(function (g, i) {
        parts.push('<div class="lshelf-lazy" data-genre="' + U.esc(g) + '" data-i="' + i + '"></div>');
      });
    }
    if (!parts.length) parts.push('<div class="lshelf-empty">Nothing here yet.</div>');
    root.innerHTML = parts.join("");
  }

  async function fillGenre(U, d, slot) {
    if (slot.dataset.done) return;
    slot.dataset.done = "1";
    var g = slot.dataset.genre;
    try {
      var r = await U.jf("Users/" + U.uid() + "/Items", {
        Genres: g, IncludeItemTypes: "Movie,Series", Recursive: true, SortBy: "DateCreated", SortOrder: "Descending",
        Limit: 16, Fields: U.FIELDS + ",UserData", EnableTotalRecordCount: true
      });
      var items = r.Items || [];
      if (!items.length) { slot.remove(); return; }
      var movies = items.filter(function (i) { return i.Type === "Movie"; }).length;
      var kind = movies >= items.length / 2 ? "movies" : "tv";
      var vid = kind === "movies" ? d.views.movies : d.views.tvshows;
      var more = vid ? "#/" + kind + "?topParentId=" + vid + "&lt_genre=" + encodeURIComponent(g) : null;
      slot.outerHTML = U.row(g, items.map(function (it) { return U.card(it); }).join(""), { more: more, cls: "genre" });
    } catch (e) { slot.remove(); }
  }

  function lazyGenres(U, d, root) {
    var slots = [].slice.call(root.querySelectorAll(".lshelf-lazy"));
    if (!("IntersectionObserver" in window)) { slots.forEach(function (s) { fillGenre(U, d, s); }); return; }
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (en) { if (en.isIntersecting) { io.unobserve(en.target); fillGenre(U, d, en.target); } });
    }, { rootMargin: "700px 0px" });
    slots.forEach(function (s) { io.observe(s); });
  }

  async function mount() {
    var U = window.__lomboShelfUI;
    if (busy || !U || !U.ready() || !onHome()) return;
    if (failedAt && Date.now() - failedAt < RETRY_MS) return;   // broken Home: stock rows stay, no call storm
    var tab = document.getElementById("homeTab");
    if (!tab || document.getElementById("lshelf-shelf")) return;
    busy = true;
    var root = document.createElement("div");
    root.id = "lshelf-shelf";
    root.className = "lshelf-shelf";
    tab.insertBefore(root, tab.firstChild);
    try {
      var d = await load(U);
      if (!root.isConnected) return;
      render(U, d, root);
      lazyGenres(U, d, root);
      HTML.classList.add("lshelf-shelf-on");
      failedAt = 0;
      // TV remote: start on Resume (or the first card) so the first Down press does not land on a header button.
      // Jellyfin's TV navigation may have focused a (now hidden) stock card; focus drops to <body> when they hide.
      if (U.isTV()) {
        var tvFocus = function () {
          var a = document.activeElement;
          var lost = !a || a === document.body || !!(a.closest && a.closest(".homeSectionsContainer"));
          if (!lost || !root.isConnected) return;
          var f = root.querySelector(".lshelf-hero .lshelf-btn.accent, .lshelf-row .focusable, .lshelf-row a, .lshelf-row .card");
          if (f && f.focus) f.focus({ preventScroll: true });
        };
        tvFocus(); setTimeout(tvFocus, 400); setTimeout(tvFocus, 1500);
      }
    } catch (e) {
      root.remove();
      failedAt = Date.now();
      HTML.classList.remove("lshelf-shelf-on");
      if (window.console) console.warn("LomboShelf failed, showing Jellyfin's Home:", e);
    } finally { busy = false; }
  }

  // Resume / Keep watching press play instead of opening the title page.
  document.addEventListener("click", function (e) {
    var a = e.target.closest && e.target.closest("#lshelf-shelf [data-play]");
    if (!a || e.ctrlKey || e.metaKey || e.shiftKey) return;
    var U = window.__lomboShelfUI;
    if (U) {
      U.play(a.getAttribute("data-play"));
      e.preventDefault();   // play() either pressed a button or already navigated to the title page
    }
  }, true);

  document.addEventListener("viewshow", function () { setTimeout(mount, 0); }, true);
  window.addEventListener("hashchange", function () { setTimeout(mount, 50); });
  setInterval(mount, 1500);   // jellyfin-web rebuilds the Home page without telling anyone
  mount();
})();
