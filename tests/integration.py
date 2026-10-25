"""Exercise the running local API without using Steam credentials or network login."""
import argparse
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--url", default="http://127.0.0.1:3241")
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--assembly", type=Path, required=True)
parser.add_argument("--data-dir", type=Path, required=True)
parser.add_argument("--protected-login")
args = parser.parse_args()
key = subprocess.check_output([args.dotnet, str(args.assembly), "dashboard-key", "--data-dir", str(args.data_dir)], text=True).strip()
created = []
checks = []


def request(path, method="GET", body=None, auth=True, custom=True, extra=None):
    headers = {"Accept": "application/json"}
    if auth:
        headers["Authorization"] = "Bearer " + key
    if custom:
        headers["X-HBR-Request"] = "1"
    if extra:
        headers.update(extra)
    data = json.dumps(body).encode() if body is not None else None
    if body is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(args.url + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw and "json" in response.headers.get("Content-Type", "") else raw.decode(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(), dict(error.headers)


def check(label, condition):
    if not condition:
        raise AssertionError(label)
    checks.append(label)
    print("PASS", label)


try:
    check("anonymous account data is blocked", request("/api/state", auth=False)[0] == 401)
    check("foreign origins are blocked", request("/api/state", extra={"Origin": "https://untrusted.example"})[0] == 403)
    check("untrusted Host is blocked", request("/api/state", extra={"Host": "untrusted.example"})[0] == 400)
    check("mutations require the app request header", request("/api/accounts", "POST", {"label": "Integration"}, custom=False)[0] == 403)
    status, _, headers = request("/api/session", "POST", {"key": key}, auth=False)
    check("dashboard pairing creates a protected session cookie", status == 200 and "httponly" in headers.get("Set-Cookie", "").lower() and "samesite=strict" in headers.get("Set-Cookie", "").lower())
    check("dashboard serves actual static UI", request("/", auth=False)[0] == 200 and "HourBoostr Remastered" in request("/", auth=False)[1])
    check("content security policy is present", "frame-ancestors 'none'" in headers.get("Content-Security-Policy", ""))
    check("empty labels are rejected", request("/api/accounts", "POST", {"label": ""})[0] == 400)
    for label in ["Integration account A", "Integration account B"]:
        status, config, _ = request("/api/accounts", "POST", {"label": label})
        check("account creation: " + label[-1], status == 200)
        created.append(config["id"])
    first, second = created
    _, config, _ = request(f"/api/accounts/{first}/settings")
    config["schedule"] = {"enabled": True, "timeZone": "UTC", "start": "22:00", "end": "03:00", "days": ["friday"]}
    config["afk"]["enabled"] = True
    config["afk"]["excludedFriends"] = ["76561198000000001"]
    config["randomCount"] = 10
    config["autoAcceptFriendRequests"] = True
    check("schedule and AFK settings persist through the API", request(f"/api/accounts/{first}/settings", "PUT", config)[0] == 200)
    saved = json.loads((args.data_dir / "settings.json").read_text())
    saved_first = next(x for x in saved["accounts"] if x["id"] == first)
    check("incoming friend auto-accept persists per account", saved_first["autoAcceptFriendRequests"] and not request(f"/api/accounts/{second}/settings")[1]["autoAcceptFriendRequests"])
    check("SteamID64 exclusions retain exact precision", saved_first["afk"]["excludedFriends"] == ["76561198000000001"])
    check("pause works", request(f"/api/accounts/{first}/pause", "POST")[0] == 200 and request(f"/api/accounts/{first}/settings")[1]["paused"])
    check("account pause is isolated", not request(f"/api/accounts/{second}/settings")[1]["paused"])
    check("resume works", request(f"/api/accounts/{first}/resume", "POST")[0] == 200 and not request(f"/api/accounts/{first}/settings")[1]["paused"])
    invalid = dict(config, randomCount=33)
    check("invalid group limits are rejected", request(f"/api/accounts/{first}/settings", "PUT", invalid)[0] == 400)
    invalid = dict(config, schedule=None)
    check("malformed settings are rejected safely", request(f"/api/accounts/{first}/settings", "PUT", invalid)[0] == 400)
    check("invalid login methods never reach Steam", request(f"/api/accounts/{first}/login", "POST", {"method": "invalid", "loginName": "TEST_LOGIN", "password": "SENSITIVE_TEST_MARKER"})[0] == 400)
    if args.protected_login:
        check("VPS account is protected from local login", request(f"/api/accounts/{first}/login", "POST", {"method": "password", "loginName": args.protected_login, "password": "SENSITIVE_TEST_MARKER"})[0] == 400)
    check("release source can be saved", request("/api/release-repository", "PUT", {"repository": "example/remaster"})[0] == 200)
    request(f"/api/accounts/{first}/settings", "PUT", config)
    check("account edits do not overwrite release source", json.loads((args.data_dir / "settings.json").read_text())["releaseRepository"] == "example/remaster")
    request("/api/release-repository", "PUT", {"repository": None})
    check("unpublished release check is explicit", request("/api/updates")[1]["configured"] is False)
    status, report, _ = request("/api/diagnostics")
    text = json.dumps(report)
    check("diagnostics exclude identity and secrets", status == 200 and all(value not in text for value in ["TEST_LOGIN", "SENSITIVE_TEST_MARKER", "Integration account A", key]))
    for path in args.data_dir.glob("*.json"):
        check("no password persisted: " + path.name, "SENSITIVE_TEST_MARKER" not in path.read_text())
    check("library of unsigned-in account is empty", request(f"/api/accounts/{first}/library")[1]["total"] == 0)
finally:
    for account in created:
        request(f"/api/accounts/{account}", "DELETE")

print(f"Integration checks passed: {len(checks)}. No Steam login was attempted.")
