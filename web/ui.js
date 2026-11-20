/*
 * LomboShelf: shared helpers for the Home shelf (home-shelf.js) and genre browsing (browse-genres.js).
 * Defines window.__lomboShelfUI; the other scripts wait for it. Settings come from window.LomboShelfConfig
 * (the plugin writes it into the page; without it the defaults in loader.js apply).
 *
 * Poster cards keep Jellyfin's own class names (.card[data-id] > .cardBox > .cardScalable) on purpose, so other
 * plugins and themes that look for poster cards still find ours.
 */
(function () {
  "use strict";
  if (window.__lomboShelfUI) return;

  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }

  function api() { return window.ApiClient; }
  function ready() { var a = api(); return !!(a && a.accessToken && a.accessToken() && a.getCurrentUserId && a.getCurrentUserId()); }
  function uid() { return api().getCurrentUserId(); }
  function cfg() { return window.LomboShelfConfig || {}; }

  // Jellyfin API. ApiClient.getJSON signs the request with the signed-in user's token.
  function jf(path, params) { return api().getJSON(api().getUrl(path, params || {})); }

  function isTV() { return document.documentElement.classList.contains("layout-tv"); }

  function minutes(ticks) { return Math.max(0, Math.round((ticks || 0) / 6e8)); }
  function dur(m) { return m >= 60 ? Math.floor(m / 60) + "h " + (m % 60 ? (m % 60) + "m" : "").trim() : m + "m"; }

  // The small grey line under a title: runtime for movies, seasons for shows.
  function sub(it) {
    if (it.Type === "Series") {
      var n = it.ChildCount;
      return n ? n + (n > 1 ? " seasons" : " season") : (it.ProductionYear || "");
    }
    if (it.Type === "Episode") return "S" + (it.ParentIndexNumber || "?") + " · E" + (it.IndexNumber || "?");
    var m = minutes(it.RunTimeTicks);
    return m ? dur(m) : (it.ProductionYear || "");
  }

  function img(id, type, tag, h) {
    var p = { quality: 88 };
    if (h) p.maxHeight = h;
    if (tag) p.tag = tag;
    return api().getUrl("Items/" + id + "/Images/" + type, p);
  }

  function poster(it) {
    var tag = it.ImageTags && it.ImageTags.Primary;
    return tag ? img(it.Id, "Primary", tag, 420) : null;
  }

  // Wide still: Thumb, then Backdrop, then Primary (episodes borrow the show's).
  function still(it) {
    if (it.ImageTags && it.ImageTags.Thumb) return img(it.Id, "Thumb", it.ImageTags.Thumb, 300);
    if (it.BackdropImageTags && it.BackdropImageTags.length) return img(it.Id, "Backdrop/0", it.BackdropImageTags[0], 300);
    if (it.ParentBackdropItemId && it.ParentBackdropImageTags && it.ParentBackdropImageTags.length) {
      return img(it.ParentBackdropItemId, "Backdrop/0", it.ParentBackdropImageTags[0], 300);
    }
    if (it.SeriesId && it.SeriesPrimaryImageTag) return img(it.SeriesId, "Primary", it.SeriesPrimaryImageTag, 300);
    return poster(it);
  }

  function href(id) { return "#/details?id=" + encodeURIComponent(id); }

  // Poster card (2:3). opts.badge: a small chip top-left ("NEW").
  function card(it, opts) {
    opts = opts || {};
    var src = poster(it);
    var title = it.SeriesName || it.Name || "";
    return '<div class="card lshelf-card" data-id="' + esc(it.Id) + '" data-type="' + esc(it.Type || "") + '">' +
      '<div class="cardBox"><div class="cardScalable">' +
      '<a class="lshelf-poster focusable" href="' + href(it.Id) + '" aria-label="' + esc(title) + '">' +
      (src ? '<img loading="lazy" decoding="async" alt="" src="' + esc(src) + '">' : "") +
      '<span class="lshelf-ph">' + esc(title) + "</span></a>" +
      (opts.badge ? '<span class="lshelf-badge' + (opts.cls ? " " + opts.cls : "") + '">' + esc(opts.badge) + "</span>" : "") +
      "</div>" +
      '<div class="lshelf-t" title="' + esc(title) + '">' + esc(title) + "</div>" +
      '<div class="lshelf-s">' + esc(opts.sub != null ? opts.sub : sub(it)) + "</div></div></div>";
  }

  // Wide tile with a progress bar (Keep watching). A click plays (home-shelf.js wires data-play).
  function wide(it) {
    var src = still(it);
    var title = it.SeriesName || it.Name || "";
    var ud = it.UserData || {};
    var pct = ud.PlayedPercentage || (it.RunTimeTicks && ud.PlaybackPositionTicks ? ud.PlaybackPositionTicks / it.RunTimeTicks * 100 : 0);
    var left = it.RunTimeTicks ? minutes(it.RunTimeTicks - (ud.PlaybackPositionTicks || 0)) : 0;
    var line = (it.Type === "Episode" ? "S" + (it.ParentIndexNumber || "?") + " · E" + (it.IndexNumber || "?") + " · " : "") +
      (left ? dur(left) + " left" : (it.Type === "Episode" ? "Next up" : ""));
    return '<div class="card lshelf-card lshelf-wide" data-id="' + esc(it.Id) + '" data-type="' + esc(it.Type || "") + '">' +
      '<div class="cardBox"><div class="cardScalable">' +
      '<a class="lshelf-still focusable" data-play="' + esc(it.Id) + '" href="' + href(it.Id) + '" aria-label="' + esc(title) + '">' +
      (src ? '<img loading="lazy" decoding="async" alt="" src="' + esc(src) + '">' : "") +
      (pct > 0 ? '<span class="lshelf-prog"><i style="width:' + Math.min(100, Math.round(pct)) + '%"></i></span>' : "") +
      "</a></div>" +
      '<div class="lshelf-t" title="' + esc(title) + '">' + esc(title) + "</div>" +
      '<div class="lshelf-s">' + esc(line) + "</div></div></div>";
  }

  // Press play on something that already has a stock card on the page (hidden or not); otherwise open its
  // title page and press its own Resume / Play button. jellyfin-web does not expose its player.
  function play(id) {
    var q = '.card[data-id="' + String(id).replace(/"/g, "") + '"] ';
    var btn = document.querySelector(q + '[data-action="resume"]') || document.querySelector(q + '[data-action="play"]');
    if (btn) { btn.click(); return true; }
    location.hash = href(id);
    var tries = 0, t = setInterval(function () {
      var b = document.querySelector(".itemDetailPage:not(.hide) .btnResume:not(.hide), .itemDetailPage:not(.hide) .btnPlay:not(.hide)");
      if (b && location.hash.indexOf(String(id)) > -1) { clearInterval(t); b.click(); }
      else if (++tries > 40) clearInterval(t);   // 8 s, then leave the person on the title page
    }, 200);
    return false;
  }

  function row(title, inner, o) {
    o = o || {};
    return '<section class="lshelf-row' + (o.cls ? " " + o.cls : "") + '"' + (o.id ? ' id="' + o.id + '"' : "") + ">" +
      '<div class="lshelf-rowh"><h2>' + esc(title) + (o.hint ? "<span>" + esc(o.hint) + "</span>" : "") + "</h2>" +
      // esc: a bare "&lt_genre" in an attribute would be read as "<_genre"
      (o.more ? '<a class="focusable" href="' + esc(o.more) + '">See all</a>' : "") + "</div>" +
      '<div class="lshelf-strip">' + inner + "</div></section>";
  }

  // The fields every list asks for.
  var FIELDS = "PrimaryImageAspectRatio,RunTimeTicks,ChildCount,ProductionYear,BasicSyncInfo";

  window.__lomboShelfUI = {
    esc: esc, ready: ready, uid: uid, jf: jf, cfg: cfg, isTV: isTV, sub: sub, img: img, poster: poster, still: still,
    href: href, card: card, wide: wide, play: play, row: row, FIELDS: FIELDS, dur: dur, minutes: minutes
  };
})();
