"""Verify the Linux container starts and retains non-Steam data on replacement."""
import json
import subprocess
import time
import urllib.error
import urllib.request

image = "hourboostr-remastered:ci"
container = "hourboostr-ci-smoke"
volume = "hourboostr-ci-smoke-data"


def docker(*args):
    return subprocess.check_output(["docker", *args], text=True).strip()


def start():
    docker("run", "-d", "--name", container, "--network", "host", "--mount", f"type=volume,source={volume},destination=/data", image)
    for _ in range(60):
        try:
            with urllib.request.urlopen("http://127.0.0.1:3241/health", timeout=2) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            time.sleep(1)
    raise AssertionError("Container dashboard did not start")


def request(path, key, method="GET", data=None):
    headers = {"Authorization": "Bearer " + key, "X-HBR-Request": "1", "Content-Type": "application/json"}
    body = None if data is None else json.dumps(data).encode()
    with urllib.request.urlopen(urllib.request.Request("http://127.0.0.1:3241" + path, data=body, headers=headers, method=method), timeout=10) as response:
        return json.load(response)


try:
    start()
    key = docker("exec", container, "dotnet", "HourBoostr.Remastered.dll", "dashboard-key")
    created = request("/api/accounts", key, "POST", {"label": "CI persistence example"})
    assert created["label"] == "CI persistence example"
    docker("stop", container)
    docker("rm", container)
    start()
    key_after = docker("exec", container, "dotnet", "HourBoostr.Remastered.dll", "dashboard-key")
    assert key_after == key
    accounts = request("/api/state", key_after)["accounts"]
    assert any(a["id"] == created["id"] and not a["loggedIn"] for a in accounts)
    print("PASS Linux container readiness, authenticated API, persistent pairing key and profile after container replacement. No Steam login attempted.")
finally:
    subprocess.run(["docker", "rm", "-f", container], check=False, stdout=subprocess.DEVNULL)
    subprocess.run(["docker", "volume", "rm", volume], check=False, stdout=subprocess.DEVNULL)
