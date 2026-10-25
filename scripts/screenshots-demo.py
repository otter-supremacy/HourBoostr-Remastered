"""Serve the real dashboard with illustrative data for public screenshots.

This loopback-only preview does not connect to Steam, read runtime account files,
accept credentials, or persist changes. It is not a production application.
"""
import argparse
from datetime import datetime, timedelta, timezone
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
from urllib.parse import parse_qs, urlparse

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--port", type=int, default=3244)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1] / "src/HourBoostr.Remastered/wwwroot"
account_id = "11111111111111111111111111111111"
now = datetime.now(timezone.utc)
stamp = now.isoformat()
games = [
    {"appId": app, "name": name, "steamMinutes": 60 * (20 + i * 9),
     "estimatedTotalSeconds": 3600 * (25 + i * 9), "idleSeconds": 3600 * 5}
    for i, (app, name) in enumerate([
        (10, "Counter-Strike"), (570, "Dota 2"), (730, "Counter-Strike 2"),
        (80, "Counter-Strike: Condition Zero"), (220, "Half-Life 2"),
        (400, "Portal"), (620, "Portal 2"), (550, "Left 4 Dead 2"),
        (440, "Team Fortress 2"), (70, "Half-Life"), (20, "Team Fortress Classic"),
        (240, "Counter-Strike: Source")])]
schedule = {"enabled": True, "timeZone": "UTC", "start": "22:00", "end": "07:00", "days": ["monday", "tuesday", "wednesday", "thursday", "friday"]}
config = {"id": account_id, "label": "Demo account", "loginName": "", "enabled": True,
          "paused": False, "presence": "online", "preferredAppId": 10,
          "selectedAppIds": [g["appId"] for g in games[:10]], "excludedAppIds": [],
          "randomCount": 10, "rotationMinutes": 60, "selectionMode": "manual",
          "pauseWhilePlaying": True, "autoAcceptFriendRequests": True,
          "schedule": schedule, "targetHours": {"10": 100},
          "afk": {"enabled": True, "message": "I am AFK right now and will respond later.",
                  "cooldownMinutes": 10, "excludedFriends": [],
                  "quietHours": {**schedule, "enabled": False}}}
view = {"id": account_id, "label": "Demo account", "state": "idling",
        "detail": "Illustrative demo: 10 games selected. No Steam session is connected.",
        "connected": True, "loggedIn": True, "hasSavedSession": True,
        "playingElsewhere": False, "steamId": None, "personaName": "Sample player",
        "libraryCount": len(games), "playingAppIds": config["selectedAppIds"],
        "playingNames": [g["name"] for g in games[:10]], "lastSteamResponse": stamp,
        "lastLibrarySync": stamp, "reportedPresence": "Illustrative demo data",
        "presenceCheckedAt": stamp, "sessionSeconds": 14400, "totalIdleSeconds": 180000,
        "reconnects": 0, "authPrompt": None, "qrSvg": None}
stats = {"games": {str(g["appId"]): g for g in games},
         "dailySeconds": {(now - timedelta(days=i)).date().isoformat(): {"10": 3600 * (i % 4 + 1), "570": 3600 * (i % 3 + 1)} for i in range(7)},
         "lastLibrarySync": stamp, "sessionSeconds": 14400}


class Preview(SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=str(root), **kw)

    def reply(self, data, code=200):
        body = json.dumps(data).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        url = urlparse(self.path)
        path = url.path
        if path == "/api/state":
            return self.reply({"version": "1.0.0-preview.1", "startedAt": (now - timedelta(hours=4)).isoformat(), "now": stamp, "accounts": [view], "events": [{"at": stamp, "level": "info", "accountId": account_id, "message": "Screenshot preview with demo data. No credentials or real account details."}], "remote": False})
        if path.endswith("/settings"):
            return self.reply(config)
        if path.endswith("/statistics"):
            return self.reply(stats)
        if path.endswith("/library"):
            query = parse_qs(url.query)
            search = query.get("search", [""])[0].lower()
            filtered = sorted([g for g in games if search in g["name"].lower()], key=lambda g: g["name"])
            return self.reply({"total": len(filtered), "games": filtered})
        if path == "/api/timezones":
            return self.reply([{"id": "UTC", "label": "UTC"}, {"id": "Asia/Kolkata", "label": "India Standard Time"}, {"id": "Europe/London", "label": "London"}])
        if path.startswith("/api/"):
            return self.reply({"error": "This screenshot preview is read-only."}, 403)
        return super().do_GET()

    def do_POST(self):
        self.reply({"error": "This screenshot preview does not accept sign-in or changes."}, 403)

    do_PUT = do_POST
    do_DELETE = do_POST


print(f"Read-only screenshot preview: http://127.0.0.1:{args.port} (illustrative data)", flush=True)
ThreadingHTTPServer(("127.0.0.1", args.port), Preview).serve_forever()
