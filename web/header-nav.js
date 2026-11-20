/*
 * LomboShelf: top bar menu order and groups (desktop header only).
 * Reorders the library links with CSS `order` and folds chosen libraries into a drop-down group. The libraries
 * and their links stay Jellyfin's own: this only reorders them, hides the ones folded into a group, and adds
 * buttons that open a small menu with the real links. Matches by the visible name, so a library with another
 * name is left where it is (at the end). Fails open: if the header is not what it expects it does nothing.
 * Not on the TV layout (legacy header).
 *
 * Settings (window.LomboShelfConfig.headerNav):
 *   order:  ["Favorites", "Movies", "Shows", "Collections"]            first to last
 *   groups: [{ "label": "Anime", "items": [["Anime", "Shows"], ["Anime Movies", "Movies"]] }]
 *           each item is [library name in the header, text shown in the menu]
 *   hide:   ["4K Movies"]                                              libraries with no button of their own
 * Without settings the script does nothing at all.
 */
(function () {
  "use strict";
  if (window.__lomboShelfHeaderNav) return;
  window.__lomboShelfHeaderNav = true;

  var conf = (window.LomboShelfConfig && window.LomboShelfConfig.headerNav) || null;
  if (!conf) return;
  var ORDER = {};
  (conf.order || []).forEach(function (name, i) { ORDER[name] = i + 1; });
  var GROUPS = (conf.groups || []).map(function (g, i) {
    return { id: "lshelf-group-" + i, label: g.label, order: (conf.order || []).indexOf(g.label) + 1 || 50 + i, items: g.items || [] };
  });
  var HIDE = {};
  (conf.hide || []).forEach(function (name) { HIDE[name] = true; });
  var CARET = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor" aria-hidden="true" style="margin-left:2px;flex:0 0 auto"><path d="M7 10l5 5 5-5z"/></svg>';

  var menu = null, openFor = null;

  function label(a) { return (a.textContent || "").replace(/\s+/g, " ").trim(); }
  function isGroup(a) { return /^lshelf-group/.test(a.id || ""); }
  function tv() { return document.documentElement.classList.contains("layout-tv") || (document.body && document.body.classList.contains("layout-tv")); }

  function closeMenu() {
    if (menu) { menu.remove(); menu = null; }
    if (openFor) { openFor.setAttribute("aria-expanded", "false"); openFor = null; }
  }
  function openMenu(btn, items) {
    closeMenu();
    menu = document.createElement("div");
    menu.className = "lshelf-menu";
    menu.setAttribute("role", "menu");
    items.forEach(function (it) {
      var a = document.createElement("a");
      a.href = it.href; a.textContent = it.text; a.className = "focusable"; a.setAttribute("role", "menuitem");
      a.addEventListener("click", closeMenu);
      menu.appendChild(a);
    });
    document.body.appendChild(menu);
    var r = btn.getBoundingClientRect();
    menu.style.top = Math.round(r.bottom + 4) + "px";
    menu.style.left = Math.max(8, Math.min(Math.round(r.left), window.innerWidth - menu.offsetWidth - 8)) + "px";
    openFor = btn; btn.setAttribute("aria-expanded", "true");
    var first = menu.querySelector("a"); if (first) first.focus();
  }
  document.addEventListener("click", function (e) { if (menu && !menu.contains(e.target) && !(openFor && openFor.contains(e.target))) closeMenu(); }, true);
  document.addEventListener("keydown", function (e) { if (e.key === "Escape" && menu) { var b = openFor; closeMenu(); if (b) b.focus(); } });
  window.addEventListener("hashchange", closeMenu);

  function arrange() {
    if (tv()) return;
    var header = document.querySelector("header.MuiAppBar-root");
    var anyLib = header && header.querySelector('a[href^="#/movies"], a[href^="#/tv"], a[href^="#/home"]');
    if (!anyLib) return;
    var bar = anyLib.parentElement;
    if (!bar || getComputedStyle(bar).display !== "flex") return;
    var links = [].slice.call(bar.children).filter(function (n) { return n.tagName === "A"; });
    var byLabel = {};
    links.forEach(function (a) { if (!isGroup(a)) byLabel[label(a)] = a; });
    var hidden = Object.assign({}, HIDE);

    GROUPS.forEach(function (g) {
      var items = g.items.map(function (p) { var a = byLabel[p[0]]; return a ? { text: p[1], href: a.getAttribute("href"), link: a } : null; }).filter(Boolean);
      var btn = document.getElementById(g.id);
      if (!items.length) { if (btn) btn.remove(); return; }   // nothing to group
      items.forEach(function (it) { hidden[label(it.link)] = true; });
      if (!btn) {
        var base = items[0].link;
        btn = base.cloneNode(true);
        btn.id = g.id;
        btn.setAttribute("href", "#");
        btn.removeAttribute("aria-current");
        btn.classList.remove("MuiButton-colorPrimary");
        btn.setAttribute("role", "button"); btn.setAttribute("aria-haspopup", "menu"); btn.setAttribute("aria-expanded", "false");
        btn.setAttribute("aria-label", g.label);
        var txt = [].slice.call(btn.childNodes).filter(function (n) { return n.nodeType === 3; })[0];
        if (txt) txt.textContent = g.label; else btn.appendChild(document.createTextNode(g.label));
        btn.insertAdjacentHTML("beforeend", CARET);
        btn.addEventListener("click", function (e) {
          e.preventDefault(); e.stopPropagation();
          if (openFor === btn) { closeMenu(); return; }
          var live = g.items.map(function (p) { var a = byLabelNow()[p[0]]; return a ? { text: p[1], href: a.getAttribute("href") } : null; }).filter(Boolean);
          openMenu(btn, live);
        }, true);
        base.parentNode.insertBefore(btn, base);
      }
      btn.style.order = String(g.order);
    });
    function byLabelNow() { var m = {}; [].slice.call(bar.children).forEach(function (a) { if (a.tagName === "A" && !isGroup(a)) m[label(a)] = a; }); return m; }

    links.forEach(function (a) {
      if (isGroup(a)) return;
      var l = label(a), isLogo = a.getAttribute("href") === "#/" || !!a.querySelector("img");   // the logo link comes first and is never grouped
      if (hidden[l]) { a.style.display = "none"; return; }
      a.style.display = "";
      a.style.order = isLogo ? "0" : String(ORDER[l] || 99);
    });
  }

  setInterval(arrange, 1000);
  arrange();
})();
