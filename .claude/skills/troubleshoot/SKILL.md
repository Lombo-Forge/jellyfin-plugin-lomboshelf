---
name: troubleshoot
description: Diagnose LomboShelf problems in the browser: shelf missing, Jellyfin's own rows still showing, wrong colours, prompt not appearing. Use when the page does not look as expected.
---

# Troubleshoot LomboShelf

Open the browser's developer console on the Jellyfin page first. Every script logs one line starting with `LomboShelf` when it gives up.

| You see | It means | Fix |
|---|---|---|
| Stock Home, console says `LomboShelf failed, showing Jellyfin's Home` | A call failed; the shelf removed itself on purpose. It retries after 5 minutes. | Read the error after it (often a 401 or a wrong server address). |
| Stock Home, no message | The scripts did not load. | Check the Network tab for `loader.js`, `ui.js`, `home-shelf.js` (404 or blocked). Check `window.LomboShelfConfig` exists before `loader.js`. |
| Both our rows and Jellyfin's rows | Our shelf drew, but `html.lshelf-shelf-on` is missing, or the stylesheet did not load. | Check `lomboshelf.css` loaded. |
| Colours unchanged | A theme value was rejected (only `#hex`, `rgb()` and sizes are accepted) or the theme is off. | Fix the value; `features.theme` must not be false. |
| No next-season dialog | Off by default; or Jellyfin Enhanced's Seerr link is not active; or the show has no TMDb id. | Turn `features.nextSeason` on and check the Jellyfin Enhanced settings. |
| Header unchanged | `headerNav` is not set, or library names differ from the settings (matching is by visible name). | Set `headerNav` and match the names exactly. |

Class names in a real Jellyfin version may differ from what the scripts expect (`#homeTab`, `header.MuiAppBar-root`); say so when reporting a bug and include the Jellyfin version.
