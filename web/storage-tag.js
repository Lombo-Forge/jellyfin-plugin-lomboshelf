/*
 * LomboShelf: SSD / HDD badge on an item's details page.
 *
 * Off unless window.LomboShelfConfig.features.storageTags is true (plugin setting "Storage tags"). Asks the plugin's own
 * route GET /LomboShelf/storage/<id> once per page view; the server looks the folder up in Tier's tiers.json and answers
 * {"tier":"ssd"|"hdd"} or 204. The browser never sees a file path. Nothing is shown on 204 or on any error.
 */
(function () {
  "use strict";
  if (window.__lomboShelfStorageTag) return;
  window.__lomboShelfStorageTag = true;
  var conf = window.LomboShelfConfig || {};
  if (!(conf.features && conf.features.storageTags)) return;

  var LABELS = { ssd: "SSD", hdd: "HDD" };
  var TITLES = { ssd: "Stored on a fast disk", hdd: "Stored on a large disk" };
  var known = null;      // { id, tier } answered for this page view; tier "" = nothing to show
  var asking = "";       // id of a question in flight

  function ui() { return window.__lomboShelfUI; }

  function show(tier) {
    var old = document.getElementById("lshelf-storage-tag");
    if (old) old.remove();
    var at = ui().titleAnchor();
    if (!tier || !at || !at.parentNode) return;
    var b = document.createElement("span");
    b.id = "lshelf-storage-tag";
    b.className = "lshelf-storage-tag " + tier;
    b.textContent = LABELS[tier];
    b.title = TITLES[tier];
    at.parentNode.insertBefore(b, at.nextSibling);
  }

  async function mount() {
    try {
      var u = ui();
      if (!u || !u.ready()) return;
      var id = u.detailsId();
      if (!id) { known = null; return; }
      if (known && known.id === id) { if (!document.getElementById("lshelf-storage-tag")) show(known.tier); return; }
      if (asking === id) return;
      asking = id;
      var r = await u.own("LomboShelf/storage/" + encodeURIComponent(id));
      asking = "";
      if (u.detailsId() !== id) return;   // the person moved on while we waited
      var tier = r.status === 200 && r.body && LABELS[r.body.tier] ? r.body.tier : "";
      known = { id: id, tier: tier };
      show(tier);
    } catch (e) {
      asking = "";
    }
  }

  document.addEventListener("viewshow", function () { known = null; setTimeout(mount, 0); }, true);
  window.addEventListener("hashchange", function () { known = null; setTimeout(mount, 50); });
  setTimeout(mount, 0);
})();
