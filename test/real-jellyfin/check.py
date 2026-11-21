"""Checks against a throwaway Jellyfin with LomboShelf and File Transformation installed. Run by run.sh."""

import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18096"
AUTH = 'MediaBrowser Client="lf", Device="lf", DeviceId="lf-test", Version="1"'
failures: list[str] = []
# Every answer the plugin gave: (path, text). The end of this file checks that none holds a token or a file path.
answers: list[tuple[str, str]] = []


def call(method, path, body=None, token=None):
    headers = {
        "Content-Type": "application/json",
        "Authorization": AUTH + (f', Token="{token}"' if token else ""),
    }
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, method=method, headers=headers, data=data)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            text = r.read().decode()
            answers.append((path, text))
            return r.status, json.loads(text) if text.strip().startswith(
                ("{", "[")
            ) else text
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        answers.append((path, text))
        try:  # an error answer that is JSON is returned parsed, like a good one
            return e.code, json.loads(text) if text.strip().startswith("{") else text[:300]
        except ValueError:
            return e.code, text[:300]


def check(name, ok):
    print(("[ ok ] " if ok else "[FAIL] ") + name)
    if not ok:
        failures.append(name)


def login(user, password):
    return call(
        "POST", "/Users/AuthenticateByName", {"Username": user, "Pw": password}
    )[1]["AccessToken"]


_, html = call("GET", "/web/index.html")
check("index.html carries the settings block", "window.LomboShelfConfig=" in html)
check(
    "index.html carries the versioned loader tag",
    "/LomboShelf/web/loader.js?v=" in html,
)
check(
    "asset route serves loader.js", call("GET", "/LomboShelf/web/loader.js")[0] == 200
)
check(
    "asset route refuses a file that is not on the list",
    call("GET", "/LomboShelf/web/notallowed.js")[0] == 404,
)
check(
    "asset route refuses path tricks",
    call("GET", "/LomboShelf/web/..%2fconfig.json")[0] == 404,
)

admin = login("admin", "lf-test-pass")
check("settings: anonymous gets 401", call("GET", "/LomboShelf/config")[0] == 401)
code, cfg = call("GET", "/LomboShelf/config", token=admin)
check("settings: admin gets 200", code == 200)
call("POST", "/Users/New", {"Name": "viewer", "Password": "viewer-pass"}, admin)
viewer = login("viewer", "viewer-pass")
check(
    "settings: a normal user gets 403",
    call("GET", "/LomboShelf/config", token=viewer)[0] == 403,
)
check(
    "settings: a bad limit gets 400",
    call(
        "POST",
        "/LomboShelf/config",
        {**cfg, "CollectionRows": [{"Name": "x", "Limit": 99}]},
        admin,
    )[0]
    == 400,
)

movies = call("GET", "/Items?Recursive=true&IncludeItemTypes=Movie", token=admin)[1][
    "Items"
]
call("POST", f"/Collections?name=Leaving%20soon&ids={movies[0]['Id']}", token=admin)
cfg["CollectionRows"] = [{"Name": "Leaving soon", "Title": "Leaving soon", "Limit": 10}]
check(
    "settings: a valid collection row is saved",
    call("POST", "/LomboShelf/config", cfg, admin)[0] == 200,
)

# ---------------------------------------------------------------------------------------------------------------
# Storage tags, leaving note, keep button and travel card, against two fake services (stubs.py, started by run.sh in
# the Jellyfin container's own network, so 127.0.0.1 there is the stub). Files and the stub log live in $LF_SCRATCH.
# ---------------------------------------------------------------------------------------------------------------
scratch = os.environ.get("LF_SCRATCH", ".scratch-jf")
farewell_token = os.environ.get("LF_FAREWELL_TOKEN", "")
visa_token = os.environ.get("LF_VISA_TOKEN", "")
if not farewell_token or not visa_token:
    print("[FAIL] LF_FAREWELL_TOKEN and LF_VISA_TOKEN must be set (run.sh does it)")
    sys.exit(1)
FAREWELL = "http://127.0.0.1:18098"
VISA = "http://127.0.0.1:18097"
TIERS_IN_JELLYFIN = "/config/contract/tiers.json"  # $scratch/config is /config in the container
LEAVING_IN_JELLYFIN = "/config/contract/leaving.json"


def hexid(value):
    return value.replace("-", "").lower()


def stub_log():
    path = os.path.join(scratch, "stubs", "seen.jsonl")
    with open(path) as f:
        return [json.loads(x) for x in f if x.strip()]


def calls_of(service, method=None):
    return [
        x
        for x in stub_log()
        if x["service"] == service and (method is None or x["method"] == method)
    ]


def dct(x):
    return x if isinstance(x, dict) else {}


def movie_id(prefix):
    return next((hexid(m["Id"]) for m in movies if m["Name"].startswith(prefix)), None)


space, ocean, city = (movie_id(n) for n in ("Space Test", "Ocean Test", "City Test"))
check("the three test movies are in the library", all((space, ocean, city)))
admin_id = hexid(call("GET", "/Users/Me", token=admin)[1]["Id"])
viewer_id = hexid(call("GET", "/Users/Me", token=viewer)[1]["Id"])
with open(os.path.join(scratch, "stubs", "admins.txt"), "w") as f:
    f.write(admin_id + "\n")

# Sample files, written as the other apps would write them (version 1) and shaped to the harness library.
with open(os.path.join(scratch, "config", "contract", "tiers.json"), "w") as f:
    json.dump(
        {
            "version": 1,
            "updated": 1,
            "units": [
                {"path": "movies/Space Test (2020)", "tier": "ssd"},
                {"path": "movies/Ocean Test (2021)", "tier": "hdd"},
            ],
        },
        f,
    )
with open(os.path.join(scratch, "config", "contract", "leaving.json"), "w") as f:
    json.dump(
        {
            "version": 1,
            "updated": 1,
            "entries": [
                {"id": space, "title": "Space Test", "days_left": 3, "can_keep": True},
                {"id": ocean, "title": "Ocean Test", "days_left": 0, "can_keep": False},
            ],
        },
        f,
    )

# 1. Everything is off by default: 404 for a signed-in user, 401 without a token.
routes = [
    ("GET", f"/LomboShelf/storage/{space}", None),
    ("GET", f"/LomboShelf/leaving/{space}", None),
    ("POST", "/LomboShelf/keep", {"itemId": space}),
    ("GET", "/LomboShelf/travel", None),
    ("POST", "/LomboShelf/travel", {"days": 3}),
]
for m, p, b in routes:
    check(f"off by default: {m} {p.split('/')[2]} gives 404 to a user", call(m, p, b, viewer)[0] == 404)
    check(f"off by default: {m} {p.split('/')[2]} gives 404 to an admin", call(m, p, b, admin)[0] == 404)
    check(f"anonymous: {m} {p.split('/')[2]} gives 401", call(m, p, b)[0] == 401)
check("index.html: the travel switch is not on before it is enabled", '"travel":true' not in str(call("GET", "/web/index.html")[1]))

# 2. Switch everything on through the settings route (admin only); the tokens go in once and never come back.
on = {
    **cfg,
    "StorageTagsEnabled": True,
    "StorageTagsAdminsOnly": True,
    "TiersFile": TIERS_IN_JELLYFIN,
    "TiersPathPrefix": "/media/",
    "KeepEnabled": True,
    "LeavingFile": LEAVING_IN_JELLYFIN,
    "FarewellUrl": FAREWELL,
    "FarewellToken": farewell_token,
    "TravelEnabled": True,
    "VisaUrl": VISA,
    "VisaToken": visa_token,
}
code, saved = call("POST", "/LomboShelf/config", on, admin)
check("settings: the features are saved", code == 200)
check(
    "settings: the answer says tokens are stored but returns neither",
    isinstance(saved, dict)
    and saved.get("FarewellTokenSet") is True
    and saved.get("VisaTokenSet") is True
    and not saved.get("FarewellToken")
    and not saved.get("VisaToken"),
)
code, again = call("GET", "/LomboShelf/config", token=admin)
check(
    "settings: reading them back gives blank tokens and the set flags",
    code == 200
    and dct(again).get("VisaTokenSet") is True
    and dct(again).get("FarewellTokenSet") is True
    and not dct(again).get("VisaToken")
    and not dct(again).get("FarewellToken"),
)
check(
    "settings: a normal user still cannot read or save them",
    call("GET", "/LomboShelf/config", token=viewer)[0] == 403
    and call("POST", "/LomboShelf/config", on, viewer)[0] == 403,
)
for bad in (
    {"VisaUrl": "http://127.0.0.1:18097/path"},
    {"VisaUrl": "ftp://127.0.0.1:18097"},
    {"VisaToken": "has a space"},
):
    check(f"settings: {list(bad)[0]} {list(bad.values())[0]!r} is refused", call("POST", "/LomboShelf/config", {**on, "VisaToken": "", **bad}, admin)[0] == 400)

call("GET", "/web/index.html")  # the page's settings block, now with the features on: section 8 checks it holds no secret

# 3. Storage tag: admin sees it, a normal user gets 204 while "administrators only" is on.
code, body = call("GET", f"/LomboShelf/storage/{space}", token=admin)
check("storage tag: admin gets ssd for the Space movie", code == 200 and body == {"tier": "ssd"})
code, body = call("GET", f"/LomboShelf/storage/{ocean}", token=admin)
check("storage tag: admin gets hdd for the Ocean movie", code == 200 and body == {"tier": "hdd"})
check("storage tag: no tier for a movie that is not in the file (204)", call("GET", f"/LomboShelf/storage/{city}", token=admin)[0] == 204)
check("storage tag: a normal user gets 204", call("GET", f"/LomboShelf/storage/{space}", token=viewer)[0] == 204)
check("storage tag: a bad id gets 204", call("GET", "/LomboShelf/storage/not-an-id", token=admin)[0] == 204)
check("storage tag: anonymous gets 401", call("GET", f"/LomboShelf/storage/{space}")[0] == 401)
call("POST", "/LomboShelf/config", {**on, "StorageTagsAdminsOnly": False, "FarewellToken": "", "VisaToken": ""}, admin)
code, body = call("GET", f"/LomboShelf/storage/{space}", token=viewer)
check("storage tag: with 'administrators only' off a normal user gets it too", code == 200 and body == {"tier": "ssd"})
call("POST", "/LomboShelf/config", {**on, "FarewellToken": "", "VisaToken": ""}, admin)

# 4. Leaving note.
code, body = call("GET", f"/LomboShelf/leaving/{space}", token=viewer)
check("leaving: a listed movie answers days_left and can_keep", code == 200 and body == {"days_left": 3, "can_keep": True})
code, body = call("GET", f"/LomboShelf/leaving/{ocean}", token=viewer)
check("leaving: a listed movie that cannot be kept says so", code == 200 and body == {"days_left": 0, "can_keep": False})
check("leaving: a movie that is not listed gets 204", call("GET", f"/LomboShelf/leaving/{city}", token=viewer)[0] == 204)
check("leaving: a bad id gets 204", call("GET", "/LomboShelf/leaving/zzz", token=viewer)[0] == 204)
check("leaving: anonymous gets 401", call("GET", f"/LomboShelf/leaving/{space}")[0] == 401)

# 5. Keep: happy path, a second press inside 10 seconds, a refusal from Farewell, and what is never sent on.
code, body = call("POST", "/LomboShelf/keep", {"itemId": space}, viewer)
check("keep: the happy path gives 200 and Farewell's message", code == 200 and body == {"ok": True, "message": "Kept for 30 more days."})
code, body = call("POST", "/LomboShelf/keep", {"itemId": space}, viewer)
check("keep: the same person again inside 10 seconds gets 429", code == 429)
code, body = call("POST", "/LomboShelf/keep", {"itemId": space}, admin)
check("keep: Farewell's refusal comes back as 422 with its message", code == 422 and body == {"ok": False, "message": "This title was already kept."})
code, body = call("POST", "/LomboShelf/keep", {"itemId": ocean}, admin)
check("keep: a title that cannot be kept gets the plain 422", code == 422 and body == {"ok": False, "message": "this title cannot be kept right now"})
code, body = call("POST", "/LomboShelf/keep", {"itemId": city}, admin)
check("keep: a title that is not leaving gets the same plain 422", code == 422 and body == {"ok": False, "message": "this title cannot be kept right now"})
check("keep: a body with an extra key gets 400", call("POST", "/LomboShelf/keep", {"itemId": space, "x": 1}, admin)[0] == 400)
check("keep: anonymous gets 401", call("POST", "/LomboShelf/keep", {"itemId": space})[0] == 401)
fw = calls_of("farewell")
check("keep: Farewell was called exactly twice (the two valid presses)", len(fw) == 2)
check("keep: every call to Farewell carried the right token", all(x["auth_ok"] for x in fw))
check("keep: Farewell got only the movie's id, as 32 hex characters", all(x["body"] == {"title": space} for x in fw))

# 6. Travel: status, open a window for the caller only, refusals, the cool-down, and a Visa that is down.
code, body = call("GET", "/LomboShelf/travel", token=viewer)
check("travel: status of a person with no window", code == 200 and body == {"active": None, "next_allowed": None, "max_days": 14})
code, body = call("POST", "/LomboShelf/travel", {"days": 3}, viewer)
check("travel: opening a window gives 200 and Visa's message", code == 200 and isinstance(body, dict) and body.get("ok") is True and "3 days" in body.get("message", ""))
code, body = call("GET", "/LomboShelf/travel", token=viewer)
check("travel: status now shows when the window ends", code == 200 and isinstance((dct(body).get("active") or {}).get("ends"), int) and body["active"]["ends"] > time.time())
check("travel: the same person again inside 10 seconds gets 429", call("POST", "/LomboShelf/travel", {"days": 3}, viewer)[0] == 429)
code, body = call("POST", "/LomboShelf/travel", {"days": 3}, admin)
check("travel: an administrator gets Visa's refusal message", code == 422 and body == {"ok": False, "message": "Use your admin tools instead."})
for bad in ({"days": 3, "user_id": admin_id}, {"days": "3"}, {"days": 0}, {"day": 3}, {}, [3]):
    check(f"travel: body {json.dumps(bad)} gets 400", call("POST", "/LomboShelf/travel", bad, viewer)[0] == 400)
check("travel: anonymous gets 401", call("POST", "/LomboShelf/travel", {"days": 3})[0] == 401)
print("... waiting 11 seconds for the cool-down")
time.sleep(11)
code, body = call("POST", "/LomboShelf/travel", {"days": 3}, viewer)
check("travel: a second window while one is open gets Visa's 422 message", code == 422 and body == {"ok": False, "message": "A window is already open."})
posts = calls_of("visa", "POST")
check("travel: Visa saw exactly the three valid requests (none of the bad bodies got through)", len(posts) == 3)
check("travel: each one was for the signed-in caller, never the body's user", [x["body"]["user_id"] for x in posts] == [viewer_id, admin_id, viewer_id])
check("travel: Visa got only user_id and days", all(set(x["body"]) == {"user_id", "days"} for x in posts))
check("travel: every call to Visa carried the right token", all(x["auth_ok"] for x in calls_of("visa")))
check("travel: status calls named the caller", {x["path"] for x in calls_of("visa", "GET")} <= {f"/v1/travel/{viewer_id}", f"/v1/travel/{admin_id}"})

code, body = call("POST", "/LomboShelf/config", {**on, "VisaToken": "wrong-token-value", "FarewellToken": ""}, admin)
code, body = call("GET", "/LomboShelf/travel", token=viewer)
check("travel: a Visa that refuses the token gives 502 with no detail", code == 502 and body == {"ok": False, "message": "could not complete the request"})
call("POST", "/LomboShelf/config", {**on, "VisaUrl": "http://127.0.0.1:18099", "VisaToken": visa_token, "FarewellToken": ""}, admin)
code, body = call("GET", "/LomboShelf/travel", token=viewer)
check("travel: a Visa that is not there gives 502 with no detail", code == 502 and body == {"ok": False, "message": "could not complete the request"})
call("POST", "/LomboShelf/config", {**on, "FarewellToken": "", "VisaToken": ""}, admin)
check("travel: the stored token and address still work after a save with a blank token", call("GET", "/LomboShelf/travel", token=viewer)[0] == 200)

# 7. Clearing: a feature switched on cannot lose its token; switched off, the routes are 404 again.
check("settings: clearing the Visa token while travel is on is refused", call("POST", "/LomboShelf/config", {**on, "VisaToken": "", "FarewellToken": "", "ClearVisaToken": True}, admin)[0] == 400)
code, _ = call("POST", "/LomboShelf/config", {**on, "TravelEnabled": False, "VisaToken": "", "FarewellToken": "", "ClearVisaToken": True}, admin)
check("settings: switching travel off and clearing its token is saved", code == 200)
check("settings: the token is reported as gone", dct(call("GET", "/LomboShelf/config", token=admin)[1]).get("VisaTokenSet") is False)
check("travel: off again gives 404", call("GET", "/LomboShelf/travel", token=viewer)[0] == 404)
check("travel: off again gives 404 on POST", call("POST", "/LomboShelf/travel", {"days": 3}, viewer)[0] == 404)
check("keep and storage still work after that", call("GET", f"/LomboShelf/leaving/{space}", token=viewer)[0] == 200 and call("GET", f"/LomboShelf/storage/{space}", token=admin)[0] == 200)

# 8. Nothing the plugin answered holds a token, and none of the feature answers hold a path or an address.
# (The settings route is for administrators and does return the file paths and addresses, never the tokens; the
# static files under /LomboShelf/web/ are code, not data.)
route_answers = [
    t for p, t in answers if p.startswith("/LomboShelf/") and not p.startswith(("/LomboShelf/config", "/LomboShelf/web/"))
]
page_answers = [t for p, t in answers if p.startswith("/web/index.html")]
check("no answer holds a token", not any(tok in t for _, t in answers for tok in (farewell_token, visa_token, "wrong-token-value")))
check("the feature routes answered, so the next checks look at something", len(route_answers) > 20 and len(page_answers) >= 1)
for needle in ("/config/contract", "tiers.json", "leaving.json", "127.0.0.1:18097", "127.0.0.1:18098", "127.0.0.1:18099"):
    check(f"no route answer or page setting holds {needle!r}", not any(needle in t for t in route_answers + page_answers))
check("no route answer holds a media file path", not any("/media/" in t for t in route_answers))

sys.exit(1 if failures else 0)
