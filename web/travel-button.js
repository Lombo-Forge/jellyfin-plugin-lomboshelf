/*
 * LomboShelf: a small collapsed "Travelling?" card on Home, so a person can allow sign-in from abroad for themselves.
 *
 * Off unless window.LomboShelfConfig.features.travel is true (plugin setting "Travel button"). GET /LomboShelf/travel answers
 * {"active": {"ends": unix}|null, "next_allowed": unix|null, "max_days": number}; the button POSTs /LomboShelf/travel with
 * {"days": N}. The server knows who is signed in and holds Visa's address and token, so this script sends only the number of
 * days with the signed-in user's own token. Only the short text the plugin returns is shown (set as text, never as HTML);
 * a failure shows a fixed line, never the raw error. Any error removes the card and Jellyfin's Home is untouched.
 */
(function () {
  "use strict";
  if (window.__lomboShelfTravel) return;
  window.__lomboShelfTravel = true;
  var conf = window.LomboShelfConfig || {};
  if (!(conf.features && conf.features.travel)) return;

  var RETRY_MS = 5 * 60 * 1000;
  var state = { info: null, msg: "", open: false, days: 0, busy: false };
  var asking = false;
  var failedAt = 0;

  function ui() { return window.__lomboShelfUI; }
  function onHome() { return /^#\/home(\?|$)/.test(location.hash) && !/tab=1/.test(location.hash); }
  function when(unix) { return new Date(unix * 1000).toLocaleString(); }

  function validInfo(b) {
    if (!b || typeof b !== "object") return null;
    var max = b.max_days;
    if (typeof max !== "number" || max < 1 || Math.floor(max) !== max) return null;
    var ends = b.active && typeof b.active === "object" && typeof b.active.ends === "number" ? b.active.ends : null;
    var next = typeof b.next_allowed === "number" ? b.next_allowed : null;
    return { max: max, ends: ends, next: next };
  }

  function statusLine(info) {
    if (info.ends !== null) return "Sign-in from abroad is allowed until " + when(info.ends) + ".";
    if (info.next !== null) return "You can open another window after " + when(info.next) + ".";
    return "No window is open.";
  }

  function render() {
    var old = document.getElementById("lshelf-travel");
    if (old) old.remove();
    var tab = document.getElementById("homeTab");
    if (!state.info || !tab) return;
    var info = state.info;
    if (!state.days) state.days = Math.min(7, info.max);
    state.days = Math.max(1, Math.min(state.days, info.max));

    var box = document.createElement("details");
    box.id = "lshelf-travel";
    box.className = "lshelf-travel";
    box.open = state.open;
    box.addEventListener("toggle", function () { state.open = box.open; });
    var sum = document.createElement("summary");
    sum.textContent = "Travelling?";
    box.appendChild(sum);

    var line = document.createElement("p");
    line.className = "lshelf-travel-status";
    line.textContent = statusLine(info);
    box.appendChild(line);

    var row = document.createElement("div");
    row.className = "lshelf-travel-row";
    var label = document.createElement("label");
    label.textContent = "Days ";
    var input = document.createElement("input");
    input.type = "number";
    input.min = "1";
    input.max = String(info.max);
    input.step = "1";
    input.value = String(state.days);
    input.className = "lshelf-travel-days";
    input.setAttribute("aria-label", "Number of days");
    input.addEventListener("input", function () { state.days = Math.floor(Number(input.value)) || 0; });
    label.appendChild(input);
    row.appendChild(label);
    var b = document.createElement("button");
    b.type = "button";
    b.className = "lshelf-btn accent lshelf-travel-btn";
    b.textContent = "Allow sign-in from abroad";
    b.disabled = state.busy;
    b.addEventListener("click", function () { allow(b, input); });
    row.appendChild(b);
    box.appendChild(row);

    if (state.msg) {
      var m = document.createElement("p");
      m.className = "lshelf-travel-msg";
      m.setAttribute("role", "status");
      m.textContent = state.msg;
      box.appendChild(m);
    }
    tab.appendChild(box);
  }

  async function refresh() {
    var r = await ui().own("LomboShelf/travel");
    state.info = r.status === 200 ? validInfo(r.body) : null;
    if (!state.info) throw new Error("no travel answer");
    render();
  }

  async function allow(button, input) {
    var days = Math.floor(Number(input.value));
    if (!state.info || !(days >= 1 && days <= state.info.max)) {
      state.msg = "Pick a number of days from 1 to " + (state.info ? state.info.max : 1) + ".";
      render();
      return;
    }
    state.days = days;
    state.busy = true;
    button.disabled = true;
    try {
      var r = await ui().own("LomboShelf/travel", { method: "POST", body: JSON.stringify({ days: days }), headers: { "Content-Type": "application/json" } });
      var m = r.body && typeof r.body.message === "string" ? r.body.message.slice(0, 200) : "";
      state.msg = (r.status === 200 || r.status === 422 || r.status === 429) && m ? m : "Could not complete the request.";
    } catch (e) {
      state.msg = "Could not complete the request.";
    }
    state.busy = false;
    try { await refresh(); } catch (e) { render(); }
  }

  async function mount() {
    try {
      var u = ui();
      if (!u || !u.ready() || !onHome() || asking) return;
      if (document.getElementById("lshelf-travel")) return;
      if (failedAt && Date.now() - failedAt < RETRY_MS) return;   // no call storm when Visa is down
      if (state.info) { render(); return; }
      asking = true;
      try { await refresh(); failedAt = 0; } finally { asking = false; }
    } catch (e) {
      failedAt = Date.now();
      state.info = null;
      var old = document.getElementById("lshelf-travel");
      if (old) old.remove();
    }
  }

  function again() { state.info = null; state.msg = ""; setTimeout(mount, 0); }
  document.addEventListener("viewshow", again, true);
  window.addEventListener("hashchange", function () { state.info = null; state.msg = ""; setTimeout(mount, 50); });
  setInterval(mount, 1500);   // jellyfin-web rebuilds the Home page without telling anyone
  setTimeout(mount, 0);
})();
