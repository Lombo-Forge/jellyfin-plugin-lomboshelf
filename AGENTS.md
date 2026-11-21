# AGENTS.md

Notes for an AI coding agent (or a person) working in this repo. `CLAUDE.md` pulls this file in.

## Contents

- [What this is](#what-this-is)
- [Layout](#layout)
- [Rules](#rules)
- [Test](#test)
- [Working efficiently](#working-efficiently)
- [Skills](#skills-claudeskills)

## What this is

LomboShelf is a Jellyfin web plugin: a streaming-style Home shelf, genre browsing, an optional next-season prompt and a
theme for jellyfin-web, installed from a plugin repository and injected through the File Transformation plugin.
`web/` (JavaScript and CSS) is tested in jsdom. `plugin/` is a C# skeleton that **compiles** and has xUnit tests for its pure logic; it loaded and injected in one throwaway Jellyfin 12.4.0 test (File Transformation 3.0.3.0); the rest is unverified (`docs/design.md`).

## Layout

| Path | What |
|---|---|
| `web/loader.js` | The one script the plugin adds to index.html: settings, theme variables, loads the rest. |
| `web/ui.js` | Shared helpers (`window.__lomboShelfUI`). |
| `web/home-shelf.js`, `browse-genres.js`, `next-season.js`, `header-nav.js`, `storage-tag.js`, `keep-button.js`, `travel-button.js` | The features. The last three are off by default and talk to the plugin's own routes (`plugin/.../Api/IntegrationController.cs`); the tokens and file paths stay on the server. |
| `web/lomboshelf.css` | Theme and layout; colours are `--lshelf-*` variables. |
| `plugin/` | C# plugin skeleton (`Jellyfin.Plugin.LomboShelf/`, its xUnit project `Jellyfin.Plugin.LomboShelf.Tests/`, `build.yaml`, `manifest.json` template). Embeds `web/`. |
| `scripts/package-plugin.sh` | Builds the plugin zip, prints its md5 and the manifest entry (the release workflow runs it). |
| `tests/web.test.mjs` | jsdom tests with a fake `ApiClient` and `fetch` (`npm test`). |
| `docs/design.md` | How files are injected, settings, versions, the C# plan, what is untested. |
| `docs/security.md` | What the token-holding features (keep, travel) trust, refuse and never log. Update it with any change to them. |
| `test/real-jellyfin/` | The real-Jellyfin harness: `run.sh`, `wizard.py`, `check.py` (routes, features, secrets), `stubs.py` (fake Farewell and Visa), `browser.py`. |
| `scripts/leak-check.sh` | Fails when private or personal strings appear. CI runs it. |

## Rules

- **Fail open.** Any error removes our block and Jellyfin's own page comes back. Never leave Jellyfin's rows hidden after a failure.
- **Escape everything** that goes into the page (`ui.esc`). Titles are untrusted. Never build CSS or JavaScript from a setting without a strict check (see the theme pattern in `loader.js`).
- **No outside calls** except the Jellyfin server (the signed-in user's `ApiClient`). The one documented exception is TMDb's backdrop image in the next-season dialog, which a setting turns off.
- **A new setting** goes in `loader.js` defaults, `docs/design.md` (Settings), `PluginConfiguration`/`IndexTransform` (a C# test compares the defaults with `loader.js`) and a test. Keep classes prefixed `lshelf-` and colours as variables.
- **Collection rows** (`collectionRows`, in `home-shelf.js`) are generic and off by default: never depend on the tool that fills a collection, never hard-code a collection name, keep the caps (5 rows, limit 1-50) in both `home-shelf.js` and the C# side. Their queries are unverified against a real Jellyfin.
- `sh test/real-jellyfin/run.sh` is the real-Jellyfin check (throwaway server, third-party File Transformation installed there only); run it after any change to the plugin or the injected scripts.
- **Not verified in a real Jellyfin (honest list):** where the badge, the note and the Travelling card land on the real pages, the new settings fields in a browser, and any call to a real Farewell, Visa or Tier. The server routes were proven on 2026-11-21 by `test/real-jellyfin/run.sh` (95 checks, fake Farewell and Visa). Keep this list in step with the README's "What is not verified".
- **Say exactly what has run in a real Jellyfin** (the one test in `docs/design.md`) and what has not. Test on a throwaway server, never someone's real one.
- **Never put private details in the repo:** real hostnames, IPs, keys, local paths. `scripts/leak-check.sh` enforces it.
- Plain language in docs. Say "streaming-style", never the name of a streaming service. Never imply the Jellyfin project made it.
- The Lombo look (every icon has the Lombo L) is in `docs/branding.md`.

## Test

```sh
npm install        # dev tools only: eslint and jsdom
npm run lint       # eslint over web/ and tests/
npm test           # node --test with jsdom
sh scripts/leak-check.sh
# C# skeleton (needs the .NET 10 SDK; in Docker build a copy so obj/ and bin/ stay out of the repo)
dotnet build -c Release plugin/Jellyfin.Plugin.LomboShelf/Jellyfin.Plugin.LomboShelf.csproj
dotnet test -c Release plugin/Jellyfin.Plugin.LomboShelf.Tests/Jellyfin.Plugin.LomboShelf.Tests.csproj
pre-commit run --all-files   # file hygiene and the leak check
```

Without Node installed: `docker run --rm -v "$PWD:/w:ro" node:22-slim sh -c "cp -r /w /tmp/w && cd /tmp/w && npm install && npx eslint web tests && npm test"`
(with a user and `HOME=/tmp` flags that suit your machine). The same checks run in CI.

## Working efficiently

- Optional tools that live in your own profile, not in this repo. Skip any you do not have.
- Graphify: build the code-only graph with `graphify update .` (never `/graphify .`, which costs tokens) and run
  `graphify query "<question>"` before grepping. `graphify-out/` is generated and gitignored; INFERRED edges are guesses.
- Ponytail: the smallest change that fully solves the task. Keep diffs small and focused.

## Skills (`.claude/skills/`)

- `ship-a-change`, `release`: get a change into `main`; cut a release.
- `install-into-my-stack`, `troubleshoot`, `adapt`: see `skills-pending/` (they could not be written into `.claude/` in the session that built this).
