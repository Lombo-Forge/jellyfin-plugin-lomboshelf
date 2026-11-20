/*
 * LomboShelf: Movies and Shows pages with genre chips, a sort menu and an "Unwatched only" switch, in place of
 * Jellyfin's grid and A-Z picker. "See all" on a Home genre row lands here with #/movies?...&lt_genre=Comedy.
 *
 * Desktop and phone layouts only (React library pages). The TV layout keeps Jellyfin's own tabs, which already
 * work with a remote. Fail-open: on any error our block is removed and Jellyfin's grid comes back.
 * Settings (window.LomboShelfConfig.browse.mergeLibraries): a list of library names, for example
 * ["Movies", "4K Movies"], whose items the Movies page lists together. Needs ui.js.
 */
(function () {
  "use strict";
  if (window.__lomboShelfBrowse) return;
  window.__lomboShelfBrowse = true;

  var PAGE = 60;
  var SORTS = {
    "Recently added": { SortBy: "DateCreated", SortOrder: "Descending" },
    "A to Z": { SortBy: "SortName", SortOrder: "Ascending" },
    "Year": { SortBy: "ProductionYear,SortName", SortOrder: "Descending" },
    "Rating": { SortBy: "CommunityRating,SortName", SortOrder: "Descending" }
  };
  var HTML = document.documentElement;
  var busy = false;

  function hashParam(k) {
    var m = location.hash.match(new RegExp("[?&]" + k + "=([^&]*)"));
    return m ? decodeURIComponent(m[1]) : "";
  }
  function pageKind() {
    if (/^#\/movies(\?|$)/.test(location.hash)) return { id: "moviesPage", type: "Movie", label: "Movies" };
    if (/^#\/tv(\?|$)/.test(location.hash)) return { id: "tvshowsPage", type: "Series", label: "Shows" };
    return null;
  }

  async function mount() {
    var U = window.__lomboShelfUI;
    var kind = pageKind();
    if (!kind) { HTML.classList.remove("lshelf-browse-on"); return; }
    if (busy || !U || !U.ready() || U.isTV()) return;
    var page = document.getElementById(kind.id);
    if (!page || page.classList.contains("hide") || page.querySelector("#lshelf-browse")) return;
    var grid = page.querySelector(".itemsContainer");
    if (!grid || !grid.parentNode) return;
    busy = true;
    var root = document.createElement("div");
    root.id = "lshelf-browse";
    root.className = "lshelf-browse";
    grid.parentNode.insertBefore(root, grid);
    var parent = hashParam("topParentId");
    var st = { genre: hashParam("lt_genre"), sort: "Recently added", unwatched: false, start: 0, total: 0, loading: false, done: false, req: 0 };
    try {
      // Optionally list several movie libraries together (the names come from the settings).
      var ancestors = [];
      var merge = ((U.cfg().browse || {}).mergeLibraries) || [];
      if (kind.type === "Movie" && merge.length > 1) {
        try {
          var views = await U.jf("UserViews", { userId: U.uid() });
          var found = ((views && views.Items) || []).filter(function (v) { return merge.indexOf(v.Name) > -1; });
          if (found.length === merge.length && (!parent || found.some(function (v) { return v.Id === parent; }))) {
            ancestors = found.map(function (v) { return v.Id; });
            parent = "";
          }
        } catch (e) { /* stay on the one library */ }
      }
      var parents = ancestors.length ? ancestors : [parent || undefined];
      var gl = await Promise.all(parents.map(function (pid) {
        return U.jf("Genres", { userId: U.uid(), IncludeItemTypes: kind.type, Recursive: true, SortBy: "SortName", ParentId: pid });
      }));
      var names = [];
      gl.forEach(function (g) { ((g && g.Items) || []).forEach(function (x) { if (names.indexOf(x.Name) < 0) names.push(x.Name); }); });
      names.sort(function (a, b) { return a.localeCompare(b); });
      root.innerHTML =
        '<div class="lshelf-bhead"><div><h1 class="visually-hidden">' + U.esc(kind.label) + '</h1><p id="lshelf-count" aria-live="polite"></p></div>' +
        '<div class="lshelf-bctl"><label class="visually-hidden" for="lshelf-sort">Sort</label>' +
        '<select id="lshelf-sort" class="lshelf-sel">' + Object.keys(SORTS).map(function (s) { return "<option>" + s + "</option>"; }).join("") + "</select>" +
        '<label class="lshelf-tog"><input type="checkbox" id="lshelf-unw"> Unwatched only</label></div></div>' +
        '<div class="lshelf-chips" role="group" aria-label="Genre">' +
        ["All"].concat(names).map(function (n) {
          var on = (n === "All" && !st.genre) || n === st.genre;
          return '<button type="button" class="lshelf-chip focusable" data-g="' + U.esc(n) + '" aria-pressed="' + on + '">' + U.esc(n) + "</button>";
        }).join("") + "</div>" +
        '<div class="lshelf-grid" id="lshelf-grid"></div><div id="lshelf-more" style="height:1px"></div>';
      var gridEl = root.querySelector("#lshelf-grid");

      async function more(reset) {
        if (reset) { st.req++; st.loading = false; }   // a reset supersedes any request still in flight
        if (st.loading || (st.done && !reset)) return;
        st.loading = true;
        var rid = st.req;
        if (reset) { st.start = 0; st.done = false; gridEl.innerHTML = ""; }
        var p = {
          userId: U.uid(), IncludeItemTypes: kind.type, Recursive: true, Limit: PAGE, StartIndex: st.start,
          Fields: U.FIELDS + ",UserData", EnableTotalRecordCount: true, ParentId: parent || undefined,
          AncestorIds: ancestors.length ? ancestors.join(",") : undefined,
          Genres: st.genre || undefined, Filters: st.unwatched ? "IsUnplayed" : undefined
        };
        var s = SORTS[st.sort]; p.SortBy = s.SortBy; p.SortOrder = s.SortOrder;
        var r = await U.jf("Items", p);
        if (rid !== st.req) return;   // superseded while fetching: the newer request owns the grid
        var items = r.Items || [];
        st.total = r.TotalRecordCount != null ? r.TotalRecordCount : st.total;
        gridEl.insertAdjacentHTML("beforeend", items.map(function (it) { return U.card(it); }).join(""));
        st.start += items.length;
        st.done = items.length < PAGE || st.start >= st.total;
        root.querySelector("#lshelf-count").textContent = st.total + " " + kind.label.toLowerCase() + (st.genre ? " in " + st.genre : "");
        if (!st.total) gridEl.innerHTML = '<div class="lshelf-empty">Nothing matches. Try another genre, or turn off "Unwatched only".</div>';
        st.loading = false;
        // A wide or tall screen can show a whole page without the sentinel ever leaving the observer's margin,
        // so it never fires again: top up until the page outgrows the viewport.
        var sentinel = root.querySelector("#lshelf-more");
        if (!st.done && sentinel && sentinel.offsetParent !== null && sentinel.getBoundingClientRect().top < window.innerHeight + 800) return more(false);
      }

      root.addEventListener("click", function (e) {
        var b = e.target.closest(".lshelf-chip");
        if (!b) return;
        st.genre = b.dataset.g === "All" ? "" : b.dataset.g;
        [].forEach.call(root.querySelectorAll(".lshelf-chip"), function (c) { c.setAttribute("aria-pressed", c === b); });
        more(true).catch(fail);
      });
      root.querySelector("#lshelf-sort").addEventListener("change", function (e) { st.sort = e.target.value; more(true).catch(fail); });
      root.querySelector("#lshelf-unw").addEventListener("change", function (e) { st.unwatched = e.target.checked; more(true).catch(fail); });
      if ("IntersectionObserver" in window) {
        new IntersectionObserver(function (en) { if (en[0].isIntersecting) more(false).catch(fail); }, { rootMargin: "800px" })
          .observe(root.querySelector("#lshelf-more"));
      }
      function fail(e) {
        if (window.console) console.warn("LomboShelf browse failed, showing Jellyfin's grid:", e);
        root.remove(); HTML.classList.remove("lshelf-browse-on");
      }
      HTML.classList.add("lshelf-browse-on");
      await more(true);
    } catch (e) {
      root.remove();
      HTML.classList.remove("lshelf-browse-on");
      if (window.console) console.warn("LomboShelf browse failed, showing Jellyfin's grid:", e);
    } finally { busy = false; }
  }

  document.addEventListener("viewshow", function () { setTimeout(mount, 0); }, true);
  window.addEventListener("hashchange", function () { setTimeout(mount, 50); });
  setInterval(mount, 1500);
  mount();
})();
