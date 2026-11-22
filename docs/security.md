# Security notes

Short notes on the parts of the plugin that handle a secret, call another service or read a file. The design and the
reasons are in [design.md](design.md#storage-tags-and-the-keep-button).

## Travel card (Visa) and keep button (Farewell)

Both are off by default. Each holds one token on the server and calls one other app; the browser never sees either.

**What it trusts**

- Jellyfin's own sign-in. The person a travel request is for is the `Jellyfin-UserId` claim of the verified request. Nothing
  in the request body can name a person: the body is exactly `{"days": N}` and any other key, a second key or a repeated
  key is refused with 400.
- The address and token an administrator saves in the plugin's settings (administrators only). The address must be
  `http://` or `https://` plus host and port, with no user name, path, query or fragment. The plugin sends the token only
  in the `Authorization` header of the one request to that address.
- Farewell's `by` (only when the admin turns on `KeepSendsName`): the Jellyfin user name of the same verified caller, looked
  up on the server from the `Jellyfin-UserId` claim, never from the request body. It is cleaned and cut to Farewell's rule (1 to
  60 printable characters, no space at either end); if the lookup fails or nothing valid is left, the request is a 502 and
  Farewell is not called. Farewell trusts the plugin's token and records the name as given: it is only as true as this plugin.
  Turn it on only with LomboFarewell 0.3.0 or newer; older versions refuse the extra key.
- Visa's and Farewell's answers only as far as the plugin reads them: a status, a short message (cut to 200 printable
  characters and shown as text) and, for the travel status, three numbers in an exact shape. Anything else is treated as
  "no answer".

**What it refuses**

- Any use while the feature is off, or while its address or token is missing: 404.
- A request without a verified user id: 401.
- A bad body, days below 1 or above 1000 (Visa applies the real limit and its own refusals, such as administrator
  accounts, an open window and the cool-down; the plugin passes its plain message through): 400 or 422.
- More than one request per person per 10 seconds, and more than one in flight: 429 with `Retry-After`. Travel and keep
  have separate counters.
- Redirects from the other app are not followed; a reply over 64 KB, a timeout of more than 10 seconds or any exception
  becomes 502 with the fixed text "could not complete the request": no address, no token, no detail.

**What it never logs or returns**

- The token is never written to the log, never put in a URL, never returned by any route (the settings route returns it
  blank plus a "token is saved" flag, and a blank token on save keeps the stored one), never put in
  `window.LomboShelfConfig` or `index.html`.
- The user name sent as `by` is in no answer to the browser, no log line and not in `window.LomboShelfConfig`. It does
  reach Farewell, which stores it (state file, keep history, `leaving.json`) by design.
- Log lines say only that a request did not complete and, for an exception, its type name. They hold no address, user id,
  token or reply text.
- File paths (`tiers.json`, `leaving.json`) and the other app's address stay on the server too.

**What still holds**

- The token sits in the plugin's XML settings file like every plugin setting: anyone who can read Jellyfin's config folder
  can read it. Use a token that only grants what the card needs.
- Any signed-in person can open a travel window for themselves, within Visa's own rules; Visa is the authority on limits.
- Plain `http://` to another machine sends the token unencrypted. Use `https://` or keep both apps on the same host or a
  private network.

## Not verified

Unit tests (a fake client, a fake HTTP handler, no network) cover this, and on 2026-11-21 `test/real-jellyfin/run.sh` proved the routes in a real
Jellyfin 12.4 against two small fake services (104 checks, including that no answer holds a token or a path). It has not been run
against a real Farewell, Visa or Tier. See the README's "What is not verified".

`KeepSendsName`: unit tests cover the body with and without it, the name cleaning, a failed lookup and that the name comes only
from the claim. The real-Jellyfin harness has a check for it (the fake Farewell records the body; it proves the caller's real
user name arrives as `by` and appears in no answer), written on 2026-11-22 but **not yet run**. Not verified: a call to a real
LomboFarewell 0.3.0 with its per-person limit on, and that the log holds no name in a real Jellyfin (the code logs a fixed line only).
