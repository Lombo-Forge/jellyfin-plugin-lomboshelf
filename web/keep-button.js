/*
 * LomboShelf: "Leaving in N days" note and a "Keep it longer" button on an item's details page.
 *
 * Off unless window.LomboShelfConfig.features.keep is true (plugin setting "Keep button"). GET /LomboShelf/leaving/<id>
 * answers {"days_left": number|null, "can_keep": bool} for a title that Farewell lists, else 204. The button POSTs
 * /LomboShelf/keep; the server holds Farewell's address and token and checks everything again, so this script only
 * sends the item id with the signed-in user's own token. Raw server messages are never shown, only the short text the
 * plugin returns (set as text, never as HTML).
 */
(function () {
  "use strict";
  if (window.__lomboShelfKeep) return;
  window.__lomboShelfKeep = true;
  var conf = window.LomboShelfConfig || {};
  if (!(conf.features && conf.features.keep)) return;

  var state = { id: "", info: null, msg: "" };
  var asking = "";

  function ui() { return window.__lomboShelfUI; }

  function note(info) {
    var d = info.days_left;
    if (typeof d !== "number" || d < 0) return "Leaving soon";
    return d === 0 ? "Leaving today" : d === 1 ? "Leaving in 1 day" : "Leaving in " + Math.floor(d) + " days";
  }

  function render() {
    var old = document.getElementById("lshelf-keep-box");
    if (old) old.remove();
    var u = ui();
    var at = u && u.titleAnchor();
    if ((!state.info && !state.msg) || !at || !at.parentNode) return;
    var box = document.createElement("div");
    box.id = "lshelf-keep-box";
    box.className = "lshelf-keep-box";
    if (state.info) {
      var n = document.createElement("span");
      n.className = "lshelf-keep-note";
      n.textContent = note(state.info);
      box.appendChild(n);
      if (state.info.can_keep === true) {
        var b = document.createElement("button");
        b.type = "button";
        b.className = "lshelf-btn accent lshelf-keep-btn";
        b.textContent = "Keep it longer";
        b.addEventListener("click", function () { keep(b); });
        box.appendChild(b);
      }
    }
    if (state.msg) {
      var m = document.createElement("span");
      m.className = "lshelf-keep-msg";
      m.setAttribute("role", "status");
      m.textContent = state.msg;
      box.appendChild(m);
    }
    at.parentNode.insertBefore(box, at.nextSibling);
  }

  async function check(id) {
    var u = ui();
    var r = await u.own("LomboShelf/leaving/" + encodeURIComponent(id));
    if (u.detailsId() !== id) return;
    var ok = r.status === 200 && r.body && typeof r.body === "object";
    state.info = ok ? { days_left: r.body.days_left, can_keep: r.body.can_keep === true } : null;
    render();
  }

  async function keep(button) {
    var id = state.id;
    button.disabled = true;
    try {
      var r = await ui().own("LomboShelf/keep", { method: "POST", body: JSON.stringify({ itemId: id }), headers: { "Content-Type": "application/json" } });
      var m = r.body && typeof r.body.message === "string" ? r.body.message.slice(0, 200) : "";
      state.msg = (r.status === 200 || r.status === 422 || r.status === 429) && m ? m : "Could not complete the request.";
    } catch (e) {
      state.msg = "Could not complete the request.";
    }
    try { await check(id); } catch (e) { render(); }
  }

  async function mount() {
    try {
      var u = ui();
      if (!u || !u.ready()) return;
      var id = u.detailsId();
      if (!id) { state = { id: "", info: null, msg: "" }; return; }
      if (state.id === id) { if (!document.getElementById("lshelf-keep-box")) render(); return; }
      if (asking === id) return;
      asking = id;
      state = { id: id, info: null, msg: "" };
      await check(id);
      asking = "";
    } catch (e) {
      asking = "";
      state.info = null;
      render();
    }
  }

  document.addEventListener("viewshow", function () { state = { id: "", info: null, msg: "" }; setTimeout(mount, 0); }, true);
  window.addEventListener("hashchange", function () { state = { id: "", info: null, msg: "" }; setTimeout(mount, 50); });
  setTimeout(mount, 0);
})();
