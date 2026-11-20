---
name: install-into-my-stack
description: Help someone try LomboShelf on their own Jellyfin server. Use when they ask to install it or set it up. Until the C# plugin is built this means the manual web route on a test server.
---

# Install LomboShelf

Be honest first: the plugin is not built yet (see `docs/design.md`), so there is no repository to install from. Ask:

1. **Is this a throwaway or test Jellyfin?** If it is their real server, recommend a test one first; nothing here has run in a real Jellyfin.
2. **Which Jellyfin version?** Only 12.x is targeted.
3. **How can they add a script to the web client?** (a custom-JavaScript plugin they already trust, or a reverse proxy that serves a modified
   index.html). Do not edit Jellyfin's files on disk.
4. Serve `web/` from a static server on their network, add the two lines from the README ("Try the web files by hand"), with a
   `window.LomboShelfConfig` that matches their wishes (colours, which features).
5. Reload the web client, open Home, and check: the shelf appears, and removing the two lines brings stock Jellyfin back.
6. For the next-season prompt they also need Jellyfin Enhanced with its Seerr connection; leave it off otherwise.
