"""Checks against a throwaway Jellyfin with LomboShelf and File Transformation installed. Run by run.sh."""

import json
import sys
import urllib.error
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18096"
AUTH = 'MediaBrowser Client="lf", Device="lf", DeviceId="lf-test", Version="1"'
failures: list[str] = []


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
            return r.status, json.loads(text) if text.strip().startswith(
                ("{", "[")
            ) else text
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:300]


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
sys.exit(1 if failures else 0)
