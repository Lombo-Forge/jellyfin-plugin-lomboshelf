# Design

## Contents

- [Status](#status)
- [What the plugin does](#what-the-plugin-does)
- [How the files get into jellyfin-web](#how-the-files-get-into-jellyfin-web)
- [Settings](#settings)
- [The web files](#the-web-files)
- [Supported Jellyfin versions](#supported-jellyfin-versions)
- [The C# skeleton plan](#the-c-skeleton-plan)
- [What is untested](#what-is-untested)
- [What was cut from the original scripts](#what-was-cut-from-the-original-scripts)

## Status

The `web/` folder (JavaScript and CSS) is written and tested against a fake jellyfin-web in jsdom. A **C# plugin
skeleton** is in `plugin/`: it compiles (`dotnet build -c Release`, .NET SDK 10.0, `Jellyfin.Controller` 12.4.0, 0
warnings, 0 errors) and the web files are embedded in the DLL. That is all that is verified. The plugin has not been
loaded by a Jellyfin server, the File Transformation hookup is a reflection call written from that plugin's README and
never run, and there are no C# tests yet. Nothing in this repository has been run in a real Jellyfin.

## What the plugin does

- **Home shelf:** a Resume card ("38 minutes left of Some Show, episode 4"), Keep watching, New this week, and one
  row per genre (filled in as you scroll), above Jellyfin's own Home rows. If anything fails, Jellyfin's rows come back.
- **Genre browsing:** the Movies and Shows pages get genre chips, a sort menu and an "Unwatched only" switch.
  "See all" on a genre row opens them already filtered.
- **Next-season prompt (off by default):** when someone finishes the last episode of a season in the library and the next
  season has aired and is not requested, ask once whether to request it, through Seerr via the Jellyfin Enhanced plugin.
- **Header menu (off by default):** reorder the library links and fold some into a drop-down.
- **Theme:** a dark, streaming-style look with an accent colour. Every colour is a CSS variable and a plugin setting.

## How the files get into jellyfin-web

jellyfin-web is a static single-page app served from the Jellyfin server. A plugin cannot edit those files on disk
(they belong to the server install and are replaced on update), so the plugin uses the **File Transformation** plugin
(a community plugin; Home Screen Sections works the same way), which lets other plugins register a transformation of
a served file in memory. The plan:

1. On start, the plugin registers a transformation of `index.html` with File Transformation. If File Transformation
   is not installed, the plugin shows a message in its settings page and does nothing else (no fallback to editing files).
2. The transformation inserts, before `</body>`, one inline script that sets `window.LomboShelfConfig = {...}` from the
   saved settings (JSON, written with a proper encoder, never string-built) and one `<script src="/LomboShelf/web/loader.js">`.
3. The plugin serves its embedded `web/*.js` and `web/lomboshelf.css` at `/LomboShelf/web/<file>` through a small controller
   (anonymous, read-only, only the files in an allow-list, `Cache-Control` by version).
4. `loader.js` fills in defaults, sets the theme variables (values are checked against a strict pattern, so a setting cannot
   inject CSS), loads the stylesheet, then loads the switched-on scripts one after another (`ui.js` first).

Nothing is sent anywhere but the Jellyfin server itself (and, only if the next-season prompt is on and art is not disabled,
a backdrop image from TMDb's public image server). There is no JavaScript Injector involved.

Jellyfin's native apps (the TV and phone apps that are not web views, and some Samsung and Roku builds) do not load the
server's web client, so they ignore all of this.

## Settings

Saved by the plugin (an XML configuration like every Jellyfin plugin) and edited in the plugin's settings page (an
embedded HTML page). They become `window.LomboShelfConfig`:

```json
{
  "theme": { "accent": "#e50914", "bg": "#141414", "surface": "#1f1f1f", "text": "#e5e5e5", "muted": "#a3a3a3", "radius": "6px" },
  "features": { "theme": true, "homeShelf": true, "browse": true, "nextSeason": false, "headerNav": false },
  "shelf": { "resume": true, "keepWatching": true, "newThisWeek": true, "genres": true, "genreLimit": 0 },
  "browse": { "mergeLibraries": ["Movies", "4K Movies"] },
  "nextSeason": { "showArt": true },
  "headerNav": { "order": ["Favorites", "Movies", "Shows"], "groups": [{ "label": "Anime", "items": [["Anime", "Shows"]] }], "hide": [] }
}
```

All keys are optional; missing ones take the defaults in `web/loader.js`. Until the plugin exists you can test by setting
`window.LomboShelfConfig` yourself before `loader.js`.

## The web files

| File | What |
|---|---|
| `web/loader.js` | Reads the settings, sets theme variables, loads the rest. |
| `web/ui.js` | Shared helpers: Jellyfin calls through `ApiClient`, poster and wide cards, rows. |
| `web/home-shelf.js` | The Home shelf. Fail-open. |
| `web/browse-genres.js` | Genre chips, sort and unwatched switch on Movies and Shows. Fail-open. |
| `web/next-season.js` | The next-season dialog. Needs Jellyfin Enhanced. |
| `web/header-nav.js` | Header order and groups. Does nothing without `headerNav` settings. |
| `web/lomboshelf.css` | Theme and layout. Colours are `--lshelf-*` variables. |

Scripts only use the signed-in user's own `ApiClient` (their token and permissions), escape every title before it goes
into the page, and never call anything outside the Jellyfin server except as noted above.

## Supported Jellyfin versions

The selectors and calls were taken from scripts that ran on a Jellyfin 12.4 web client. Jellyfin 12.x is the target
(the plugin package for the server is published as `Jellyfin.Controller` 12.0.0 to 12.4.0). Older versions are not
supported; other 12.x minors are expected to work but nothing here was run on any of them.

## The C# skeleton plan

Built as a skeleton in `plugin/` (compiles; see Status for what that does and does not prove):

```
plugin/
  build.yaml                          jprm metadata (guid, version, targetAbi 12.4.0.0, net10.0)
  manifest.json                       plugin repository manifest TEMPLATE (placeholder checksum, URL and timestamp)
  Jellyfin.Plugin.LomboShelf/
    Jellyfin.Plugin.LomboShelf.csproj   net10.0, Jellyfin.Controller 12.4.0, embeds ../../web/*.js and *.css
    Plugin.cs                           BasePlugin<PluginConfiguration>, IHasWebPages, fixed GUID
    PluginServiceRegistrator.cs         registers the registrar and the startup service
    Configuration/PluginConfiguration.cs  the settings as properties (defaults equal web/loader.js)
    Configuration/configPage.html       settings form (embedded), ApiClient.getPluginConfiguration / updatePluginConfiguration
    Api/WebAssetsController.cs          GET /LomboShelf/web/{file}: embedded files, allow-list, anonymous
    Services/IndexTransform.cs          pure: settings -> window.LomboShelfConfig JSON; html -> html with both script tags
    Services/IFileTransformationRegistrar.cs   the seam behind which the File Transformation call lives
    Services/FileTransformationRegistrar.cs    reflection call to File Transformation (UNVERIFIED, see below)
    Services/PatchRequestPayload.cs     { "contents": "..." } handed to the callback
    Services/StartupRegistration.cs     IHostedService: registers on start, logs a warning when it cannot
```

Not in the skeleton: `Api/ConfigController.cs`, the xUnit project, `headerNav.groups` in the settings page (the config
JSON sends an empty list), release packaging.

**File Transformation contract, as far as it was confirmed.** From that plugin's README (read once, from its GitHub
page): a plugin cannot reference it directly (load-context issues), so it finds the assembly whose full name contains
`.FileTransformation` in `AssemblyLoadContext.All`, gets type `Jellyfin.Plugin.FileTransformation.PluginInterface`, and
invokes static `RegisterTransformation` with a payload holding `id` (GUID), `fileNamePattern` (regex),
`callbackAssembly`, `callbackClass` and `callbackMethod`. The callback gets `{ "contents": "..." }`. **Not confirmed:**
the parameter type of `RegisterTransformation` (the code guesses a Newtonsoft `JObject`, else a JSON string), the exact
callback signature and return handling (the code uses `static string Transform(PatchRequestPayload)`, the shape another
plugin uses), and which File Transformation versions work with Jellyfin 12.x (its README says each release is tied to
one Jellyfin version). If the call fails, the plugin logs a warning and injects nothing.

Steps still to do: (1) install File Transformation and this plugin in a throwaway Jellyfin 12.4, read its source or
logs, and fix the registration; (2) `IndexTransform` tests (xUnit); (3) build the zip with `jprm` and fill
`manifest.json`; (4) the release workflow to publish it; (5) test desktop, a phone browser and a TV browser.

## What is untested

- Everything in a real jellyfin-web. The tests use a fake `ApiClient`, a fake page and a fake `fetch`; they show the scripts
  build the expected DOM, escape titles, respect settings and fail open. They do not show that the class names and page
  structure of a real Jellyfin 12.x match (`#homeTab`, `.homeSectionsContainer`, `header.MuiAppBar-root`, `#moviesPage`).
- The CSS was only read, not looked at in a browser after the port; the original's visual checks were not repeated.
- The TV layout and focus rules (carried over from a version that was checked on real TVs) have not been re-checked.
- The next-season flow and the Jellyfin Enhanced endpoints it calls were taken from the original and not checked against
  that plugin's current version.
- File Transformation: the registration code in `plugin/` was written from its README and never run; parameter type, callback signature and version support are unconfirmed.
- The C# plugin has only been compiled. Loading in Jellyfin, the settings page, the asset route, the manifest and installing from a plugin repository were never run. No C# tests exist.
- The wordmark's leaning "F" in the banner did not render as leaning in the PNG (the SVG attribute is ignored by the renderer used).

## What was cut from the original scripts

The original lived inside a private setup and called its admin service. Removed because they depended on it: the
"Leaving soon" row, "Your requests are ready", "Coming this week", the Requests panel and header pill, delete and
storage tags, extra header buttons, a bundled logo, and the web font download. The Jellyfin-only parts (Resume, Keep
watching, New this week, genre rows, genre browsing, next-season prompt, header order) were kept and generalised: class
prefix `lshelf-`, colours as variables, names and library groups as settings.
