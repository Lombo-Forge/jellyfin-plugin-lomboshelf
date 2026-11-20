<p align="center"><img src="assets/banner.svg" alt="LomboShelf" width="100%"></p>

<p align="center">
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/github/license/Lombo-Forge/jellyfin-plugin-lomboshelf"></a>
  <a href="https://github.com/Lombo-Forge/jellyfin-plugin-lomboshelf/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/Lombo-Forge/jellyfin-plugin-lomboshelf/ci.yml?branch=main&label=CI"></a>
  <a href="https://github.com/Lombo-Forge/jellyfin-plugin-lomboshelf/releases"><img alt="Release" src="https://img.shields.io/github/v/release/Lombo-Forge/jellyfin-plugin-lomboshelf?include_prereleases"></a>
  <img alt="Jellyfin 12.x" src="https://img.shields.io/badge/Jellyfin-12.x-blue">
  <img alt="Status: skeleton" src="https://img.shields.io/badge/status-skeleton%2C%20unverified-orange">
</p>

# LomboShelf

A streaming-style Home for Jellyfin's web client: a Resume card, Keep watching, New this week, one row per genre, genre
chips on the Movies and Shows pages, an optional next-season prompt, and a dark theme whose colours you set.

> **Status: not a working plugin yet.** The JavaScript and CSS in [`web/`](web) are written and tested against a fake
> web client (jsdom). A C# plugin skeleton in [`plugin/`](plugin) compiles (`dotnet build -c Release`), but it has never
> been loaded by a Jellyfin server, its File Transformation hookup is unverified, and there is no release or
> plugin repository yet (see [docs/design.md](docs/design.md)). Nothing here has been run in a real Jellyfin. Until the plugin exists, the
> only way to try the web files is the manual route below, on a throwaway server.

## Contents

- [What you get](#what-you-get)
- [Try the web files by hand](#try-the-web-files-by-hand)
- [Install in a minute (planned)](#install-in-a-minute-planned)
- [Settings](#settings)
- [What is not verified](#what-is-not-verified)
- [FAQ](#faq)
- [Contributing and licence](#contributing-and-licence)

## What you get

| Piece | What it does |
|---|---|
| Home shelf | Resume, Keep watching, New this week, genre rows. If it fails, Jellyfin's own rows come back. |
| Genre browsing | Chips, sort and "Unwatched only" on Movies and Shows. |
| Next-season prompt | Off by default. Asks once whether to request the next season, through Seerr via Jellyfin Enhanced. |
| Header menu | Off by default. Reorder library links, fold some into a drop-down. |
| Theme | Dark, with colours as settings. Can be switched off. |

Works in the browser and the phone apps that embed Jellyfin's web client. Jellyfin's native apps ignore it.

## Try the web files by hand

On a **throwaway** Jellyfin 12.x, not your real one. Serve the `web/` folder from any static web server on your network, then
add this to the page (for example with a custom-JavaScript plugin you already trust), before the script tag:

```html
<script>window.LomboShelfConfig = { theme: { accent: "#e50914" }, features: { nextSeason: false } };</script>
<script src="http://your-test-host:8080/loader.js"></script>
```

`loader.js` loads `lomboshelf.css` and the other scripts from the same folder. Remove the two lines to go back to stock.

## Install in a minute (planned)

This is how it is meant to work once the plugin is built and released; it does not work today.

1. Install the **File Transformation** plugin.
2. Dashboard, Plugins, Repositories, add the LomboShelf repository URL, then install LomboShelf and restart Jellyfin.
3. Open the plugin's settings and pick your colours and features.

## Settings

The keys are listed in [docs/design.md](docs/design.md#settings). All are optional.

## What is not verified

- Nothing has run in a real Jellyfin. The selectors and API calls come from scripts that ran on a Jellyfin 12.4 web client
  elsewhere; the class names in a real page were not re-checked.
- The C# plugin in `plugin/` only compiles. The settings page, the asset route, the File Transformation hookup (written from its README, never run) and the manifest template are all untested; there are no C# tests.
- The CSS was ported and read, not viewed in a browser. TV and phone layouts were not re-checked.
- The next-season prompt depends on Jellyfin Enhanced's Seerr endpoints, not checked against its current version.
- JavaScript is checked with ESLint and 9 jsdom tests (`npm test`); there is no CSS linter.

## FAQ

**Will it break my Home?** The shelf only hides Jellyfin's rows after it has drawn itself, and removes itself on any error.

**Does it send data anywhere?** Only to your Jellyfin server. The next-season dialog can show a backdrop from TMDb's image server; set `nextSeason.showArt` to false to avoid that.

**Why a plugin and not the JavaScript Injector?** Fewer moving parts to install and update, and settings in one place. The injector route still works for the web files.

## Contributing and licence

Contribution, security and conduct rules come from the [Lombo-Forge organisation](https://github.com/Lombo-Forge/.github). Licensed under [GPL-3.0](LICENSE) (a Jellyfin plugin links Jellyfin's GPL packages). Credits are in [NOTICE.md](NOTICE.md). Not affiliated with or endorsed by the Jellyfin project.
