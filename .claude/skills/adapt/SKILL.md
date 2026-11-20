---
name: adapt
description: Change what LomboShelf shows or how it looks (rows, colours, settings, header order) without breaking Jellyfin's own pages. Use when the user wants a different row, a new setting, other colours, or a new feature script.
---

# Adapt LomboShelf

1. **Colours and look:** colours are `--lshelf-*` variables set by `web/loader.js` from the settings; change defaults in
   the `defaults.theme` object and in the `:root` block of `web/lomboshelf.css` together.
2. **A new row or change a row:** edit `render()` and `load()` in `web/home-shelf.js`. Keep it fail-open: an error must remove
   `#lshelf-shelf` and leave Jellyfin's rows visible. Add a jsdom test in `tests/web.test.mjs` with fake data.
3. **A new setting:** add it to `defaults` in `web/loader.js`, read it where it is used with a safe fallback, document it in
   `docs/design.md` (Settings), and test it.
4. **A new feature script:** put it in `web/`, guard with `window.__lomboShelfX`, make it do nothing unless its setting is on,
   add it to the list in `loader.js`, and test it.
5. **Always:** escape titles with `ui.esc`; call only Jellyfin through `ApiClient`; prefix classes with `lshelf-`.
6. **Check:** `npm run lint`, `npm test`, `sh scripts/leak-check.sh`. Then, before claiming anything works, try it on a throwaway
   Jellyfin; say what was not tried.
