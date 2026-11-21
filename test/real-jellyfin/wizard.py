"""Finish a fresh Jellyfin's setup wizard (user admin / lf-test-pass) and add a Movies library. Run by run.sh."""

import json
import sys
import time
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18096"
AUTH = 'MediaBrowser Client="lf", Device="lf", DeviceId="lf-test", Version="1"'


def call(method, path, body=None, token=None):
    headers = {
        "Content-Type": "application/json",
        "Authorization": AUTH + (f', Token="{token}"' if token else ""),
    }
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, method=method, headers=headers, data=data)
    with urllib.request.urlopen(req, timeout=60) as r:
        text = r.read().decode()
        return json.loads(text) if text.strip().startswith(("{", "[")) else text


call(
    "POST",
    "/Startup/Configuration",
    {
        "UICulture": "en-US",
        "MetadataCountryCode": "US",
        "PreferredMetadataLanguage": "en",
    },
)
call("GET", "/Startup/User")
call("POST", "/Startup/User", {"Name": "admin", "Password": "lf-test-pass"})
call(
    "POST",
    "/Startup/RemoteAccess",
    {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False},
)
call("POST", "/Startup/Complete")
token = call(
    "POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": "lf-test-pass"}
)["AccessToken"]
call(
    "POST",
    "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=true",
    {"LibraryOptions": {"PathInfos": [{"Path": "/media/movies"}]}},
    token,
)
for _ in range(30):
    time.sleep(2)
    items = call("GET", "/Items?Recursive=true&IncludeItemTypes=Movie", token=token)
    if items["TotalRecordCount"] >= 3:
        break
print(
    f"[ ok ] library has {items['TotalRecordCount']} movies"
    if items["TotalRecordCount"] >= 3
    else "[FAIL] library scan"
)
sys.exit(0 if items["TotalRecordCount"] >= 3 else 1)
