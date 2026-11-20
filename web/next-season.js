/*
 * LomboShelf: "Season N is out. Request it?"
 *
 * Runs inside jellyfin-web for signed-in users. When someone finishes the last episode of a season that is in the
 * library, and the next season has aired but is not in the library or requested, it asks once. "Request" asks
 * Seerr through the Jellyfin Enhanced plugin's Seerr connection, as that person (their permissions and auto-approve
 * rules apply). "Not now" is remembered per person, show and season in this browser.
 *
 * Needs: the Jellyfin Enhanced plugin with its Seerr integration on, and the show to have a TMDb id.
 * Off unless window.LomboShelfConfig.nextSeason.enabled is true (the plugin setting "Next season prompt").
 * Without Jellyfin Enhanced every call below fails quietly and nothing is shown.
 */
(function () {
  "use strict";
  if (window.__lomboShelfNextSeason) return;
  window.__lomboShelfNextSeason = true;
  var conf = (window.LomboShelfConfig && window.LomboShelfConfig.nextSeason) || {};
  if (!conf.enabled) return;

  var STORE = "lomboshelf.nextSeason.v1";
  var MediaStatus = { UNKNOWN: 1, DELETED: 7 };
  var RequestDeclined = 3;
  var inFlight = new Set();
  var dialogOpen = false;   // only one dialog at a time

  function remembered() {
    try { return JSON.parse(localStorage.getItem(STORE) || "{}"); } catch (e) { return {}; }
  }
  function remember(key) {
    try { var m = remembered(); m[key] = Date.now(); localStorage.setItem(STORE, JSON.stringify(m)); } catch (e) { /* private mode */ }
  }

  function api() { return window.ApiClient; }
  function userId() { return api().getCurrentUserId(); }

  async function je(path, options) {
    var a = api();
    var res = await fetch(a.getUrl("/JellyfinEnhanced" + path), Object.assign({}, options, {
      headers: {
        "Authorization": 'MediaBrowser Token="' + a.accessToken() + '"',
        "X-Jellyfin-User-Id": userId(),
        "Content-Type": "application/json"
      }
    }));
    if (!res.ok) throw new Error("HTTP " + res.status);
    return res.status === 204 ? null : res.json();
  }

  async function jf(path) {
    var a = api();
    var res = await fetch(a.getUrl(path), { headers: { "Authorization": 'MediaBrowser Token="' + a.accessToken() + '"' } });
    if (!res.ok) throw new Error("HTTP " + res.status);
    return res.json();
  }

  // ------------------------------------------------------------------ dialog
  function ask(seriesName, season, backdropUrl) {
    return new Promise(function (resolve) {
      var wrap = document.createElement("div");
      wrap.className = "lshelf-dialog";
      wrap.setAttribute("role", "dialog");
      wrap.setAttribute("aria-modal", "true");
      wrap.setAttribute("aria-labelledby", "lshelf-ns-title");
      wrap.innerHTML =
        '<div class="lshelf-dialog-box">' +
          '<div class="lshelf-dialog-art"></div>' +
          '<div class="lshelf-dialog-body">' +
            '<div id="lshelf-ns-title" class="lshelf-dialog-title"></div>' +
            '<div class="lshelf-dialog-text">You finished the last episode in the library. Want the next season?</div>' +
            '<div class="lshelf-dialog-btns">' +
              '<button type="button" data-a="no" class="lshelf-btn">Not now</button>' +
              '<button type="button" data-a="yes" class="lshelf-btn accent">Request</button>' +
            "</div>" +
          "</div>" +
        "</div>";
      wrap.querySelector("#lshelf-ns-title").textContent = "Season " + season + " of " + seriesName + " is out";
      var art = wrap.querySelector(".lshelf-dialog-art");
      if (backdropUrl) art.style.backgroundImage = "linear-gradient(0deg, var(--lshelf-surface) 0%, transparent 45%), url('" + backdropUrl + "')";
      else art.remove();
      // resolves "yes", "no" (explicit Not now: remembered) or "close" (Escape / click outside: not remembered)
      function done(answer) { document.removeEventListener("keydown", onKey); wrap.remove(); resolve(answer); }
      function onKey(e) { if (e.key === "Escape") done("close"); }
      wrap.addEventListener("click", function (e) {
        var b = e.target.closest("button");
        if (b) done(b.dataset.a === "yes" ? "yes" : "no");
        else if (e.target === wrap) done("close");
      });
      document.addEventListener("keydown", onKey);
      document.body.appendChild(wrap);
      wrap.querySelector('[data-a="yes"]').focus();
    });
  }

  function toast(text) {
    var t = document.createElement("div");
    t.className = "lshelf-toast";
    t.textContent = text;
    document.body.appendChild(t);
    setTimeout(function () { t.remove(); }, 4500);
  }

  // ------------------------------------------------------------------ logic
  async function onPlayed(itemId) {
    if (inFlight.has(itemId)) return;
    inFlight.add(itemId);
    try {
      var uid = userId();
      var ep = await jf("/Users/" + uid + "/Items/" + itemId);
      if (!ep || ep.Type !== "Episode" || ep.ParentIndexNumber == null) return;

      // Last episode of its season that we have?
      var eps = await jf("/Shows/" + ep.SeriesId + "/Episodes?userId=" + uid + "&seasonId=" + ep.SeasonId);
      var last = Math.max.apply(null, (eps.Items || []).map(function (e) { return e.IndexNumberEnd || e.IndexNumber || 0; }));
      if ((ep.IndexNumberEnd || ep.IndexNumber || 0) < last) return;

      var series = await jf("/Users/" + uid + "/Items/" + ep.SeriesId);
      var tmdbId = Number(series && series.ProviderIds && (series.ProviderIds.Tmdb || series.ProviderIds.tmdb));
      if (!tmdbId) return;
      var next = ep.ParentIndexNumber + 1;
      var key = uid + ":" + tmdbId + ":" + next;
      if (remembered()[key]) return;

      // Already in the library?
      var seasons = await jf("/Shows/" + ep.SeriesId + "/Seasons?userId=" + uid);
      if ((seasons.Items || []).some(function (s) { return s.IndexNumber === next && (s.ChildCount || 0) > 0; })) return;

      var status = await je("/jellyseerr/user-status").catch(function () { return null; });
      if (status && status.active === false) return;   // this person is not linked to Seerr

      var tv = await je("/jellyseerr/tv/" + tmdbId);
      var s = (tv.seasons || []).find(function (x) { return x.seasonNumber === next; });
      if (!s || !s.episodeCount || !s.airDate || new Date(s.airDate) > new Date()) return;
      var media = tv.mediaInfo || {};
      var st = ((media.seasons || []).find(function (x) { return x.seasonNumber === next; }) || {}).status || MediaStatus.UNKNOWN;
      if (st !== MediaStatus.UNKNOWN && st !== MediaStatus.DELETED) return;   // pending / downloading / available
      var requested = (media.requests || []).some(function (r) {
        return r.status !== RequestDeclined && (r.seasons || []).some(function (x) { return x.seasonNumber === next; });
      });
      if (requested) return;

      // The backdrop is TMDb's public image CDN; leave it out (conf.showArt = false) for a fully local setup.
      var art = (conf.showArt !== false && typeof tv.backdropPath === "string" && /^\/[A-Za-z0-9._-]+$/.test(tv.backdropPath))
        ? "https://image.tmdb.org/t/p/w780" + tv.backdropPath : null;
      if (dialogOpen) return;
      dialogOpen = true;
      var answer;
      try { answer = await ask(series.Name, next, art); } finally { dialogOpen = false; }
      if (answer === "yes" || answer === "no") remember(key);
      if (answer !== "yes") return;
      try {
        await je("/jellyseerr/request/tv/" + tmdbId + "/seasons", {
          method: "POST",
          body: JSON.stringify({ mediaType: "tv", mediaId: tmdbId, seasons: [next] })
        });
        toast("Season " + next + " of " + series.Name + " is on its way.");
      } catch (e) {
        toast("Could not request season " + next + ". Try requesting it in Seerr.");
      }
    } catch (e) {
      console.debug("[LomboShelf next season]", e);
    } finally {
      inFlight.delete(itemId);
    }
  }

  // ------------------------------------------------------------------ hook
  // The server pushes "UserDataChanged" over the web socket when an item is marked played.
  function onMessage(_e, msg) {
    if (!msg || msg.MessageType !== "UserDataChanged") return;
    var data = msg.Data || {};
    if (data.UserId && data.UserId.replace(/-/g, "") !== String(userId()).replace(/-/g, "")) return;
    (data.UserDataList || []).forEach(function (ud) { if (ud.Played) onPlayed(ud.ItemId); });
  }

  function attach() {
    var a = api();
    if (!a || !a.accessToken || !a.accessToken() || !a.getCurrentUserId()) return false;
    // Newer jellyfin-web: ApiClient.subscribe delivers socket messages.
    if (typeof a.subscribe === "function") {
      a.subscribe(["UserDataChanged"], function (msg) {
        onMessage(null, { MessageType: "UserDataChanged", Data: msg && msg.Data });
      });
      return true;
    }
    if (window.Events && typeof window.Events.on === "function") {   // older jellyfin-web
      window.Events.on(a, "message", onMessage);
      return true;
    }
    return false;
  }
  (function wait(n) {
    if (attach()) return;
    if (n < 600) setTimeout(function () { wait(n + 1); }, 1000);
  })(0);
})();
