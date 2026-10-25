"""Opt-in tests against a signed-in local Steam account. Never submits credentials."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import urllib.request

p = argparse.ArgumentParser()
p.add_argument("--url", default="http://127.0.0.1:3241")
p.add_argument("--dotnet", default="dotnet")
p.add_argument("--assembly", type=Path, required=True)
p.add_argument("--data-dir", type=Path, required=True)
p.add_argument("--account-id", required=True)
args = p.parse_args()
key = subprocess.check_output([args.dotnet, str(args.assembly), "dashboard-key", "--data-dir", str(args.data_dir)], text=True).strip()


def api(path, method="GET", body=None):
    data = None if body is None else json.dumps(body).encode()
    headers = {"Authorization": "Bearer " + key, "X-HBR-Request": "1"}
    if body is not None:
        headers["Content-Type"] = "application/json"
    with urllib.request.urlopen(urllib.request.Request(args.url + path, data=data, method=method, headers=headers), timeout=30) as response:
        raw = response.read()
        return json.loads(raw) if raw else None


def account():
    return next(a for a in api("/api/state")["accounts"] if a["id"] == args.account_id)


def wait_for(predicate, label):
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        value = account()
        if predicate(value):
            print("PASS", label)
            return value
        time.sleep(1)
    raise AssertionError(label + " timed out; state=" + account()["state"])


prefix = f"/api/accounts/{args.account_id}"
current = account()
assert current["loggedIn"], "Sign in manually before running this test"
library = api(prefix + "/statistics")["games"]
owned = {int(k) for k in library}
assert len(owned) >= 2
preferred = 10 if 10 in owned else min(owned)
manual = [preferred] + sorted(owned - {preferred})[:1]
config = api(prefix + "/settings")
config.update(selectionMode="manual", selectedAppIds=manual, preferredAppId=preferred, paused=False, enabled=True)
config["schedule"]["enabled"] = False
api(prefix + "/settings", "PUT", config)
wait_for(lambda x: x["playingAppIds"] == manual, "two owned games selected with preferred game first")
api(prefix + "/pause", "POST")
wait_for(lambda x: x["state"] == "paused" and not x["playingAppIds"], "pause clears games without signing out")
api(prefix + "/resume", "POST")
wait_for(lambda x: x["state"] == "idling" and x["playingAppIds"] == manual, "resume restores games")
config = api(prefix + "/settings")
config["schedule"]["enabled"] = True
config["schedule"]["days"] = []
api(prefix + "/settings", "PUT", config)
wait_for(lambda x: x["state"] == "scheduled" and not x["playingAppIds"], "schedule pause applies to a real Steam session")
config = api(prefix + "/settings")
config["schedule"]["enabled"] = False
config["targetHours"] = {str(preferred): 0.001}
api(prefix + "/settings", "PUT", config)
wait_for(lambda x: preferred not in x["playingAppIds"], "reached estimated hour target excludes the game")
config = api(prefix + "/settings")
config["targetHours"] = {}
config.update(selectionMode="random", randomCount=min(10, len(owned)), selectedAppIds=[], excludedAppIds=[])
api(prefix + "/settings", "PUT", config)
api(prefix + "/randomize", "POST")
view = wait_for(lambda x: len(x["playingAppIds"]) == min(10, len(owned)), "random owned-game group starts")
assert view["playingAppIds"][0] == preferred
assert set(view["playingAppIds"]).issubset(owned)
assert len(view["playingAppIds"]) == len(set(view["playingAppIds"]))
print("PASS all selected games are owned and unique")
before = api(prefix + "/statistics")["sessionSeconds"]
time.sleep(3)
after = api(prefix + "/statistics")["sessionSeconds"]
assert after > before
print("PASS real session statistics advance while idling")
print("Selected games:", ", ".join(view["playingNames"]))
print("Live smoke checks completed. The local account remains idling the final random group.")
