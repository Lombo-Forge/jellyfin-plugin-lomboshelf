# Design

## Contents

- [Status](#status)
- [What the plugin does](#what-the-plugin-does)
- [How the files get into jellyfin-web](#how-the-files-get-into-jellyfin-web)
- [Settings](#settings)
- [Storage tags and the keep button](#storage-tags-and-the-keep-button)
- [The travel card](#the-travel-card)
- [The web files](#the-web-files)
- [Supported Jellyfin versions](#supported-jellyfin-versions)
- [The C# skeleton plan](#the-c-skeleton-plan)
- [What is untested](#what-is-untested)
- [What was cut from the original scripts](#what-was-cut-from-the-original-scripts)

## Status

The `web/` folder (JavaScript and CSS) is written and tested against a fake jellyfin-web in jsdom. A **C# plugin
skeleton** is in `plugin/`: it compiles (`dotnet build -c Release`, .NET SDK 10.0, `Jellyfin.Controller` 12.4.0), the
web files are embedded in the DLL, and 166 xUnit tests (`dotnet test`) cover the pure logic: the `index.html` edit, the
settings JSON, the asset allow-list, settings checks and the defaults. On 2026-11-21 it ran once in a throwaway Jellyfin 12.4.0 with File Transformation 3.0.3.0: both plugins loaded, the registration worked, `index.html` carried the settings and the versioned `loader.js` tag, the asset and settings routes answered as designed (anonymous 401, normal user 403, admin 200, bad input 400), and headless Chromium showed the dark theme, a "New this week" row and a collection row on desktop and phone widths. Everything else listed under "What is untested" has not run.

## What the plugin does

- **Home shelf:** a Resume card ("38 minutes left of Some Show, episode 4"), Keep watching, New this week, and one
  row per genre (filled in as you scroll), above Jellyfin's own Home rows. If anything fails, Jellyfin's rows come back.
- **Collection rows (off by default):** any Jellyfin collection, by exact name, as a Home row between New this week and the
  genre rows (for example "Leaving soon" from a separate tool; Shelf does not depend on it). Same look and fail-open
  behaviour as the other rows.
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
embedded HTML page, which reads and saves them through `GET`/`POST /LomboShelf/config`, administrators only; the
controller checks colours against the same pattern as `loader.js` and the header-menu groups against their shape before
saving, and answers 400 with the problems). In the page the groups are a JSON text box (`HeaderNavGroupsJson`). They
become `window.LomboShelfConfig`:

```json
{
  "theme": { "accent": "#e50914", "bg": "#141414", "surface": "#1f1f1f", "text": "#e5e5e5", "muted": "#a3a3a3", "radius": "6px" },
  "features": { "theme": true, "homeShelf": true, "browse": true, "nextSeason": false, "headerNav": false, "storageTags": false, "keep": false, "travel": false },
  "shelf": { "resume": true, "keepWatching": true, "newThisWeek": true, "genres": true, "genreLimit": 0 },
  "collectionRows": [{ "name": "Leaving soon", "title": "Going soon", "limit": 20 }],
  "browse": { "mergeLibraries": ["Movies", "4K Movies"] },
  "nextSeason": { "showArt": true },
  "headerNav": { "order": ["Favorites", "Movies", "Shows"], "groups": [{ "label": "Anime", "items": [["Anime", "Shows"]] }], "hide": [] }
}
```

**`collectionRows`** is a list of `{ name, title?, limit? }`, empty by default (feature off). `name` is the collection's exact
name (case-sensitive, trimmed). `title` is the row heading (default: the name). `limit` is the item count, 1-50, default 16.
At most 5 rows; the script ignores extra rows, rows without a name and out-of-range limits (clamped), and the plugin's
settings check rejects them on save (limit 0 means "default"; `IndexTransform` clamps again). Per row the browser asks
the signed-in user's `ApiClient` for `Users/{id}/Items?IncludeItemTypes=BoxSet&Recursive=true&NameStartsWith=<name>`,
keeps the result whose `Name` equals the setting exactly (the server match is looser), then lists
`Users/{id}/Items?ParentId=<collection id>&Limit=<limit>`. A missing collection, an empty one or any error removes
just that row. Every name, title and item title is escaped by `ui.esc`. In the settings page the rows are a text box, one
`name | title | limit` line each (C# type `CollectionRow`). **Unverified:** these two queries were never sent to a real
Jellyfin; the order of items is whatever the server returns for a collection's children.

All keys are optional; missing ones take the defaults in `web/loader.js`. Until the plugin exists you can test by setting
`window.LomboShelfConfig` yourself before `loader.js`.

## Storage tags and the keep button

Two optional features, both **off by default**, that read what the other Lombo apps write. The file formats are the
other apps' promises and live in the Lombo hub repository's `docs/contracts.md` (LomboTier's `tiers.json`, LomboFarewell's
`leaving.json` and keep call). This page only says how Shelf uses them.

**Settings** (`PluginConfiguration`, checked by `ConfigSanitizer`):

| Setting | Default | Meaning |
|---|---|---|
| `StorageTagsEnabled` | false | Turns the SSD / HDD badge on. |
| `StorageTagsAdminsOnly` | true | Only administrators get a tier; everyone else gets 204. |
| `TiersFile` | empty | Absolute path of `tiers.json` as the Jellyfin process sees it. |
| `TiersPathPrefix` | empty | The part of a Jellyfin item path to strip so the rest equals a `path` in `tiers.json`, for example `/media/`. No `..`. |
| `KeepEnabled` | false | Turns the leaving note and keep button on. |
| `LeavingFile` | empty | Absolute path of `leaving.json` as Jellyfin sees it. |
| `FarewellUrl` | empty | `http` or `https`, host and port only (no user info, path, query or fragment). Saved as `scheme://host[:port]`. |
| `KeepSendsName` | false | Adds `"by"` (the signed-in person's Jellyfin user name) to the keep call. Turn on only with LomboFarewell 0.3.0 or newer; older versions refuse the extra key. Not sent to the browser. |
| `FarewellToken` | empty | **Secret.** At most 256 visible characters, no spaces or control characters. |

The save check answers 400 with the list of problems for: a bad URL, a relative or `..` file path, a prefix with `..`, a bad
or over-long token, and a feature switched on without what it needs (storage tags need `TiersFile`; keep needs
`LeavingFile`, `FarewellUrl` and the token). Only two booleans reach the browser, `features.storageTags` and `features.keep`
(each true only when the feature is on **and** its required settings are present): no path, address or token is ever in
`window.LomboShelfConfig` or `index.html`.

**The token.** `GET /LomboShelf/config` returns it blank plus `FarewellTokenSet` (true when one is stored). A `POST` with a
blank token keeps the stored one; `ClearFarewellToken: true` clears it. Both extra fields are not saved in the XML. It is
never logged and appears in no response. The settings page shows a password input that is always empty.
It is stored in the plugin's XML file, like every plugin setting, so anyone who can read Jellyfin's config folder can read it.

**Routes** (new controller; any signed-in user, not admin-only; every answer is JSON with `Cache-Control: no-store`; each
answers 404 when its feature is off; ids are 32 hex characters, Jellyfin's dashed form is accepted):

| Route | Answer |
|---|---|
| `GET /LomboShelf/storage/{itemId}` | `200 {"tier":"ssd"\|"hdd"}`, or `204` for: unknown item, item not visible to this user, not a Movie, Season or Episode, no matching unit, unreadable file, wrong file version, or (admins-only on) a caller who is not an administrator. |
| `GET /LomboShelf/leaving/{itemId}` | `200 {"days_left": number\|null, "can_keep": bool}` when the id is in `leaving.json` and the user can see the item, else `204`. |
| `POST /LomboShelf/keep` | Body exactly `{"itemId":"<id>"}` (1 KB at most, no other keys). `200 {"ok":true,"message":...}`, `422 {"ok":false,"message":...}`, `400` bad body, `429` with `Retry-After`, `502 {"ok":false,"message":"could not complete the request"}`. |

*Storage resolution.* The server looks the item up with Jellyfin's library, checks the caller may see it, takes its file path
(a season's own folder), strips `TiersPathPrefix` (the path must start with it, at a folder boundary), normalises it
(`/`, no empty parts, any `..` means no match) and finds the longest `tiers.json` unit whose `path` equals that path or is a
parent folder of it. So a movie matches its folder and an episode matches its season folder. A path is never returned.

*Keep.* The server checks: the feature is on, the id is well formed, the caller may see the item, and the id is in
`leaving.json` with `can_keep` true (every failure of the last two answers the same 422, so a person learns nothing about
titles they cannot see). Then it calls `FarewellUrl/v1/keep` with `Authorization: Bearer <token>`, `Content-Type:
application/json` and the body `{"title":"<32 hex id>"}` (with `KeepSendsName` on, `{"title":"<32 hex id>","by":"<name>"}`): 10 second timeout, redirects not followed, the answer read up to 64 KB.
Farewell 200 becomes 200 and Farewell 422 becomes 422 (its message cut to 200 printable characters); anything else, a timeout
or an exception becomes 502 with a fixed message, and the log line has no address, token or detail. One keep per person per 10
seconds (429 with `Retry-After`) and one keep in flight at a time (429, retry in 2 seconds).

*The name (`KeepSendsName`).* Off by default, because Farewell 0.2.0 accepts exactly one key and answers 400 to a second. When on, the
server looks the signed-in user up by the verified `Jellyfin-UserId` claim in Jellyfin's user manager and sends that user name as
`by`, cleaned to Farewell's rule: characters Farewell would call not printable (control and format characters, line breaks, spaces other than a plain
one, private use, unassigned) become spaces, the ends are trimmed and it is cut to 60 characters (counted as code points). If the user is
not found or nothing valid is left, the answer is the generic 502 and Farewell is not called (a blank name is never sent, and the
person's 10 second wait is not used up). The browser's body still has exactly one key, so a body that names someone else is a 400.
The name is in no answer, log line or settings answer. Farewell then refuses a keep with no name when its per-person limit is on, which is why this setting exists.

*Reading the files.* Only the configured path is opened. At most 4 MB; the file must be JSON with `"version": 1` (any other
version means "no data", as the contracts say); the parsed result is cached by last-write time and size; any problem
(missing, unreadable, too big, bad JSON) gives no data and never an exception. A symbolic link at the configured path is
followed by the operating system as usual: point the setting at the real file.

**Why the server does this.** The browser must never hold the Farewell token (anyone who opens the page could read it and
call Farewell directly, with no per-person rule) and must never learn where files live. So the plugin holds the token and
makes the call, turns a folder into just "ssd" or "hdd", and repeats the visibility check Jellyfin itself would do for the
person. The routes use Jellyfin's token header, not cookies, so a page on another site cannot call them on someone's behalf.
What still holds: anyone who can sign in and see a keepable title can press keep (Farewell's own rules limit how often a
title can be kept), and Farewell does not know who pressed it.

**The two scripts.** On an item details page (`#/details?id=...`, once per page view, the signed-in person's own token):

- `storage-tag.js` asks `GET /LomboShelf/storage/{id}` and, on 200 with `ssd` or `hdd`, adds a small badge after the title. Nothing
  on 204 or on any error.
- `keep-button.js` asks `GET /LomboShelf/leaving/{id}`. On 200 it shows "Leaving in N days" ("Leaving soon" when null,
  "Leaving today" at 0) and, when `can_keep`, a "Keep it longer" button. The button posts the id, disables itself while waiting,
  shows the returned message (as text, cut to 200 characters; a failure shows only "Could not complete the request.") and
  asks again.

`ui.js` gained `own()` (the signed-in user's own call to a plugin route), `detailsId()` and `titleAnchor()` for these two.

## The travel card

A third optional feature, **off by default**: a small collapsed "Travelling?" card on Home where a signed-in person can
allow sign-in from abroad for themselves, through LomboVisa's self-service API (the shared promise is in the Lombo hub
repository's `docs/contracts.md`). It has the same shape as the keep button: the server holds Visa's address and token, the
browser never sees them. The security notes are in [security.md](security.md).

**Settings:**

| Setting | Default | Meaning |
|---|---|---|
| `TravelEnabled` | false | Turns the card and its routes on. |
| `VisaUrl` | empty | `http` or `https`, host and port only, saved as `scheme://host[:port]` (same rule as `FarewellUrl`). |
| `VisaToken` | empty | **Secret**, same rules and handling as `FarewellToken`: at most 256 visible characters; returned blank with `VisaTokenSet`; a blank save keeps it; `ClearVisaToken: true` clears it; neither extra field is saved in the XML. |

A save with travel on and no address or token is refused (400). `features.travel` is the only travel value that reaches the
browser, true only when the feature is on **and** both settings are present.

**Routes** (any signed-in user; `Cache-Control: no-store`; 404 when off, 401 without a verified user id):

| Route | Answer |
|---|---|
| `GET /LomboShelf/travel` | `200 {"active": {"ends": <unix seconds>}\|null, "next_allowed": <unix seconds>\|null, "max_days": n}`, or `502` (fixed text) when Visa does not answer in exactly that shape. |
| `POST /LomboShelf/travel` | Body exactly `{"days": N}` (1 KB at most, one key, an integer from 1 to 1000; the plugin's cap is only a sanity check, Visa applies the real limit). `200 {"ok":true,"message":...}`, `422 {"ok":false,"message":...}` (Visa's own refusal text, cleaned to 200 printable characters), `400` bad body, `429` with `Retry-After`, `502 {"ok":false,"message":"could not complete the request"}`. |

The person is always the verified `Jellyfin-UserId` claim; it is sent to Visa as 32 hex characters
(`GET {VisaUrl}/v1/travel/<id>` and `POST {VisaUrl}/v1/travel` with `{"user_id": "<id>", "days": N}`, `Authorization: Bearer <token>`).
10 second timeout, redirects not followed, answer read up to 64 KB. Visa 200 becomes 200, Visa 422 becomes 422, anything else
(401, 429, 5xx, a timeout, an exception, a body of the wrong shape) becomes 502 with a fixed message and a log line with no
address, token or detail. One travel request per person per 10 seconds and one in flight (its own counters, apart from keep).
Administrator accounts get no special case in the plugin: Visa refuses them and the plugin passes its message through.
`GET` has no cool-down of its own (the card asks once per Home view).

**`travel-button.js`** (only when `features.travel`): on Home it asks `GET /LomboShelf/travel` and adds a collapsed card at
the end of the Home tab: the end of the open window or the time another is allowed, a number box for days (1 to `max_days`)
and an "Allow sign-in from abroad" button. The button posts only `{"days": N}`, disables itself while waiting, shows the
returned message as text (cut to 200 characters; a failure shows only "Could not complete the request.") and asks again.
Days outside 1 to `max_days` are not sent. On any error or an answer of the wrong shape the card is not shown, and the
script waits 5 minutes before asking again.

## The web files

| File | What |
|---|---|
| `web/loader.js` | Reads the settings, sets theme variables, loads the rest. |
| `web/ui.js` | Shared helpers: Jellyfin calls through `ApiClient`, poster and wide cards, rows. |
| `web/home-shelf.js` | The Home shelf. Fail-open. |
| `web/browse-genres.js` | Genre chips, sort and unwatched switch on Movies and Shows. Fail-open. |
| `web/next-season.js` | The next-season dialog. Needs Jellyfin Enhanced. |
| `web/header-nav.js` | Header order and groups. Does nothing without `headerNav` settings. |
| `web/storage-tag.js` | SSD / HDD badge on a details page. Off unless `features.storageTags`. Fail-open. |
| `web/keep-button.js` | "Leaving in N days" note and "Keep it longer" button. Off unless `features.keep`. Fail-open. |
| `web/travel-button.js` | Collapsed "Travelling?" card on Home. Off unless `features.travel`. Fail-open. |
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
  Jellyfin.Plugin.LomboShelf.Tests/     xUnit project (net10.0); runs against the plugin project and the real web/ folder
  Jellyfin.Plugin.LomboShelf/
    Jellyfin.Plugin.LomboShelf.csproj   net10.0, Jellyfin.Controller 12.4.0, embeds ../../web/*.js and *.css
    Plugin.cs                           BasePlugin<PluginConfiguration>, IHasWebPages, fixed GUID
    PluginServiceRegistrator.cs         registers the registrar and the startup service
    Configuration/PluginConfiguration.cs  the settings as properties (defaults equal web/loader.js)
    Configuration/configPage.html       settings form (embedded), ApiClient.getPluginConfiguration / updatePluginConfiguration
    Api/WebAssetsController.cs          GET /LomboShelf/web/{file}: embedded files, allow-list, anonymous
    Api/ConfigController.cs             GET/POST /LomboShelf/config: settings for the settings page, admin only, checked on save; the token is never returned
    Api/IntegrationController.cs        storage, leaving, keep and travel routes (signed-in users), see Storage tags and the keep button, The travel card
    Services/ContractFiles.cs           reads tiers.json / leaving.json (size, version, cache); StorageResolver; id normalising
    Services/Keep.cs                    FarewellClient (the keep call), KeepGuard (cooldown, one in flight)
    Services/Travel.cs                  VisaClient (the two travel calls), TravelGuard (its own cooldown and in-flight gate)
    Services/ItemAccess.cs              the seam in front of Jellyfin's library and users
    Services/FeatureGate.cs             "feature is on" in one place
    Services/IndexTransform.cs          pure: settings -> window.LomboShelfConfig JSON; html -> html with both script tags
    Services/WebAssets.cs               the allow-list of served files (exact match, so no path can get through)
    Services/ConfigSanitizer.cs         checks and cleans settings before they are saved
    Services/HeaderNavGroups.cs         parses and checks the header-menu groups text
    Services/IFileTransformationRegistrar.cs   the seam behind which the File Transformation call lives
    Services/FileTransformationRegistrar.cs    reflection call to File Transformation (verified once, see below)
    Services/PatchRequestPayload.cs     { "contents": "..." } handed to the callback
    Services/StartupRegistration.cs     IHostedService: registers on start, logs a warning when it cannot
```

The xUnit tests check: the script tags go before `</body>` (any case; appended when there is none) and a second pass
changes nothing; the settings JSON cannot close the script tag (`<`, `>`, `&` are escaped) and still reads back to the
original text; bad header-menu groups fall back to an empty list; only the ten listed files are served and
`..`, slashes, encoded dots, other case, empty and `null` names get a 404 (the controller is called directly); the list
equals the files in `web/` and the embedded resources; and the default settings produce exactly the defaults written in
`web/loader.js` (the test reads that file). They do not run Jellyfin, the settings page's JavaScript, authorization on
the config route (the `RequiresElevation` policy name is taken from Jellyfin, not exercised) or the plugin's XML saving.

Packaging: `scripts/package-plugin.sh <version>` publishes the plugin, zips only `Jellyfin.Plugin.LomboShelf.dll`, and
prints the zip's md5 and a manifest entry (`lomboshelf_<version>.zip`, the file layout jprm makes). It was run once in
a container (with a stand-in `zip`, as the .NET image has none) and produced a zip with the dll; the zip was never
installed anywhere. The release workflow runs it on a tag and attaches the zip. `jprm` itself was not used or tried.

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
logs, and fix the registration; (2) fill the hub repository's `manifest.json` from a real release (the script prints the
entry); (3) test the settings page, desktop, a phone browser and a TV browser.

## What is untested

- Everything in a real jellyfin-web. The tests use a fake `ApiClient`, a fake page and a fake `fetch`; they show the scripts
  build the expected DOM, escape titles, respect settings and fail open. They do not show that the class names and page
  structure of a real Jellyfin 12.x match (`#homeTab`, `.homeSectionsContainer`, `header.MuiAppBar-root`, `#moviesPage`).
- Collection rows: the BoxSet lookup and the children query ran against a real Jellyfin 12.4.0 once (one collection, one title). The settings-page text box for them was never opened, and ordering and limits with a large collection are unchecked.
- The CSS was only read, not looked at in a browser after the port; the original's visual checks were not repeated.
- The TV layout and focus rules (carried over from a version that was checked on real TVs) have not been re-checked.
- The next-season flow and the Jellyfin Enhanced endpoints it calls were taken from the original and not checked against
  that plugin's current version.
- File Transformation: verified once against 3.0.3.0 on Jellyfin 12.4.0 (its source, `PluginInterface.RegisterTransformation(JObject)`, was read and matches the payload `id, fileNamePattern, callbackAssembly, callbackClass, callbackMethod`; the callback is called with `{contents}` and a returned string replaces the page). Load order the other way round, other versions and upgrades are unverified.
- Storage tags, the keep button and the travel routes: run on 2026-11-21 in a throwaway Jellyfin 12.4.0 (`test/real-jellyfin/run.sh`; now 104 checks) against fake Farewell and Visa services and sample `tiers.json` and `leaving.json`. That proved the plain `[Authorize]` policy (12.4 has no policy named `DefaultAuthorization`), the `Jellyfin-UserId` claim, the library lookup, every route's answers, the cool-down and that no answer holds a token or a path. Not run: the badge position on the real details page, the new settings fields on the real page, and a real Farewell, Visa or Tier.
- The travel card: its routes ran in the real-Jellyfin check above against a fake Visa. Not run: where the card lands on the real Home page and a call to a real LomboVisa.
- Genre rows, "Continue watching", the next-season flow, the header menu and the settings page's screen (only its routes were called), TV layouts and real clients, installing from a plugin repository (zip, checksum, manifest), and any real library were never run.
- The wordmark's leaning "F" in the banner did not render as leaning in the PNG (the SVG attribute is ignored by the renderer used).

## What was cut from the original scripts

The original lived inside a private setup and called its admin service. Removed because they depended on it: the
"Leaving soon" row, "Your requests are ready", "Coming this week", the Requests panel and header pill, delete and
storage tags, extra header buttons, a bundled logo, and the web font download. The Jellyfin-only parts (Resume, Keep
watching, New this week, genre rows, genre browsing, next-season prompt, header order) were kept and generalised: class
prefix `lshelf-`, colours as variables, names and library groups as settings.
