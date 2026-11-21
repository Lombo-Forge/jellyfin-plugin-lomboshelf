"""Two tiny fake services for the real-Jellyfin check: a fake Farewell (keep) and a fake Visa (travel). Run by run.sh.

They share the throwaway Jellyfin container's network, so the plugin reaches them at 127.0.0.1 and nothing else can.
Standard library only. Each request is appended to /stub/seen.jsonl (service, method, path, whether the bearer token was
right, the parsed body) so check.py can see what the plugin sent. The token itself is never written anywhere.
/stub/admins.txt (written by check.py) lists the user ids the fake Visa treats as administrators.
"""

import json
import os
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STUB = "/stub"
FAREWELL_TOKEN = os.environ["LF_FAREWELL_TOKEN"]
VISA_TOKEN = os.environ["LF_VISA_TOKEN"]
HEX = re.compile(r"^[0-9a-f]{32}$")
MAX_DAYS = 14

lock = threading.Lock()
kept: set[str] = set()
windows: dict[str, int] = {}


def log(service, method, path, auth_ok, body):
    line = json.dumps(
        {
            "service": service,
            "method": method,
            "path": path,
            "auth_ok": auth_ok,
            "body": body,
        }
    )
    with lock, open(os.path.join(STUB, "seen.jsonl"), "a") as f:
        f.write(line + "\n")


def admins():
    try:
        with open(os.path.join(STUB, "admins.txt")) as f:
            return {x.strip() for x in f if x.strip()}
    except OSError:
        return set()


class Base(BaseHTTPRequestHandler):
    service = ""
    token = ""

    def log_message(self, *args):
        pass

    def send(self, status, obj):
        data = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def read_body(self):
        try:
            n = int(self.headers.get("Content-Length", "0"))
            if n < 0 or n > 4096:
                return None
            return json.loads(self.rfile.read(n).decode())
        except (ValueError, UnicodeDecodeError):
            return None

    def handle_any(self, method):
        body = self.read_body() if method == "POST" else None
        auth_ok = self.headers.get("Authorization") == "Bearer " + self.token
        log(self.service, method, self.path, auth_ok, body)
        if not auth_ok:
            return self.send(401, {"ok": False, "message": "unauthorized"})
        with lock:
            status, obj = self.answer(method, self.path, body)
        return self.send(status, obj)

    def do_GET(self):
        self.handle_any("GET")

    def do_POST(self):
        self.handle_any("POST")

    def answer(self, method, path, body):
        return 404, {"ok": False, "message": "not found"}


class Farewell(Base):
    service = "farewell"
    token = FAREWELL_TOKEN

    def answer(self, method, path, body):
        if path != "/v1/keep" or method != "POST":
            return 404, {"ok": False, "message": "not found"}
        title = body.get("title") if isinstance(body, dict) and len(body) == 1 else None
        if not isinstance(title, str) or not HEX.match(title):
            return 400, {"ok": False, "message": "bad request"}
        if title in kept:
            return 422, {"ok": False, "message": "This title was already kept."}
        kept.add(title)
        return 200, {"ok": True, "message": "Kept for 30 more days."}


class Visa(Base):
    service = "visa"
    token = VISA_TOKEN

    def state(self, user):
        ends = windows.get(user)
        active = {"id": 1, "ends": ends} if ends and ends > time.time() else None
        return {"ok": True, "active": active, "next_allowed": None, "max_days": MAX_DAYS}

    def answer(self, method, path, body):
        m = re.match(r"^/v1/travel/([0-9a-f]{32})$", path)
        if method == "GET" and m:
            return 200, self.state(m.group(1))
        if method != "POST" or path != "/v1/travel":
            return 404, {"ok": False, "message": "not found"}
        ok = (
            isinstance(body, dict)
            and set(body) == {"user_id", "days"}
            and isinstance(body["user_id"], str)
            and HEX.match(body["user_id"])
            and isinstance(body["days"], int)
            and not isinstance(body["days"], bool)
        )
        if not ok:
            return 400, {"ok": False, "message": "bad request"}
        user, days = body["user_id"], body["days"]
        if user in admins():
            return 422, {"ok": False, "message": "Use your admin tools instead."}
        if not 1 <= days <= MAX_DAYS:
            return 422, {"ok": False, "message": f"Pick 1 to {MAX_DAYS} days."}
        if self.state(user)["active"]:
            return 422, {"ok": False, "message": "A window is already open."}
        windows[user] = int(time.time()) + days * 86400
        out = self.state(user)
        out["message"] = f"Sign-in from abroad is allowed for {days} days."
        return 200, out


def serve(handler, port):
    server = ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()


serve(Visa, 18097)
serve(Farewell, 18098)
print("ready", flush=True)
while True:
    time.sleep(3600)
