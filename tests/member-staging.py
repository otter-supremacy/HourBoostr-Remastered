"""Exercise the member web API and the restricted moderation socket."""
import contextlib
import http.client
import http.cookiejar
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

PORT = 5483
ORIGIN = f"http://localhost:{PORT}"
HEADERS = {"Origin": ORIGIN, "X-HBR-Member": "1", "Content-Type": "application/json"}


def browser():
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))


def call(client, path, expected, method="GET", data=None, headers=None):
    request = urllib.request.Request(ORIGIN + path, method=method, headers=HEADERS if headers is None else headers,
                                     data=None if data is None else json.dumps(data).encode())
    try:
        result = client.open(request, timeout=5)
    except urllib.error.HTTPError as error:
        result = error
    with result:
        value = result.read()
        assert result.status == expected, (path, result.status, expected, value)
        return json.loads(value) if value and "application/json" in result.headers.get("Content-Type", "") else value


class Admin(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__("localhost", timeout=5)
        self.path = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(5)
        self.sock.connect(str(self.path))


def admin(path, route, expected, payload=None):
    with contextlib.closing(Admin(path)) as conn:
        conn.request("GET" if payload is None else "POST", "/internal/" + route,
                     None if payload is None else json.dumps(payload), {"Content-Type": "application/json"})
        response = conn.getresponse()
        value = response.read()
        assert response.status == expected, (route, response.status, value)
        return json.loads(value) if value else None


def wait_for_health():
    for _ in range(60):
        try:
            return call(browser(), "/api/member/config", 200)
        except urllib.error.URLError:
            time.sleep(.25)
    raise RuntimeError("Service did not start")


def check_password_accounts(sock):
    path = "/auth/password/"
    alice_email = "Alice@example.test"
    fixture = {"email": alice_email, "password": "a unique passphrase for testing"}
    account, guest, second_session = browser(), browser(), browser()
    assert call(guest, "/api/member/config", 200)["passwords"]
    call(account, path + "register", 403, "POST", fixture, {**HEADERS, "Origin": "https://other.example"})
    call(account, path + "register", 400, "POST", {**fixture, "password": "short"})
    first_code = call(account, path + "register", 200, "POST", fixture)["recoveryCode"]
    registered = call(account, "/api/member/me", 200)
    assert registered["email"] == alice_email.lower()
    appearance = "/api/member/appearance"
    custom = {"status": "online", "badge": "vr", "customText": "Demo custom status"}
    call(guest, appearance, 401)
    assert call(account, appearance, 200)["badge"] == "normal"
    call(account, appearance, 400, "PUT", {**custom, "badge": "unsupported"})
    call(account, appearance, 400, "PUT", {**custom, "customText": "two\nlines"})
    call(account, appearance, 403, "PUT", custom, {**HEADERS, "Origin": "https://other.example"})
    assert call(account, appearance, 200, "PUT", custom) == custom
    # The simulated Google fixture has the same email. Password signup must create its own member.
    assert len([member for member in admin(sock, "members", 200)["members"] if member["email"] == alice_email.lower()]) == 2
    call(guest, path + "register", 409, "POST", {**fixture, "email": alice_email.upper()})
    call(guest, "/api/member/me", 401)
    for _ in range(8):
        call(guest, path + "login", 400, "POST", {**fixture, "password": "wrong test passphrase"})
    call(guest, path + "login", 400, "POST", fixture)
    new_password = {**fixture, "password": "different test passphrase for recovery"}
    call(guest, path + "recover", 400, "POST", {**new_password, "recoveryCode": "f" * 32})
    next_code = call(guest, path + "recover", 200, "POST", {**new_password, "recoveryCode": first_code})["recoveryCode"]
    assert first_code != next_code
    call(account, "/api/member/me", 401)
    call(guest, "/api/member/me", 200)
    assert call(guest, appearance, 200) == custom
    call(account, path + "recover", 400, "POST", {**new_password, "recoveryCode": first_code})
    call(second_session, path + "login", 400, "POST", fixture)
    call(second_session, path + "login", 204, "POST", new_password)
    assert call(second_session, "/api/member/me", 200)["id"] == registered["id"]
    admin(sock, f"members/{registered['id']}/ban", 204, {"deviceId": "2" * 32})
    call(second_session, "/api/member/me", 401)
    call(second_session, path + "login", 400, "POST", new_password)
    call(second_session, path + "recover", 400, "POST", {**fixture, "recoveryCode": next_code})


def main(dotnet, assembly):
    with tempfile.TemporaryDirectory(prefix="members-", dir=os.environ.get("MEMBER_TEST_TEMP")) as directory:
        data = Path(directory)
        sock = data / "admin.sock"
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Testing", Members__Data=directory, Members__Origin=ORIGIN,
                   Members__Port=str(PORT), Members__Socket=str(sock), Members__LiveSteam="false", Members__PasswordAccounts="true")
        with open(data / "service.log", "w", encoding="utf-8") as log:
            def start(restore=None):
                if restore:
                    import shutil
                    shutil.copyfile(restore, data / "members.sqlite")
                process = subprocess.Popen([dotnet] if assembly == "-" else [dotnet, assembly], env=env, stdout=log, stderr=log)
                wait_for_health()
                return process

            process = start()
            try:
                alice, bob, stranger = browser(), browser(), browser()
                call(stranger, "/api/member/me", 401)
                call(stranger, "/internal/members", 404)
                call(stranger, "/control/actions", 404, "POST")
                call(alice, "/auth/test/alice", 403, "POST", headers={**HEADERS, "Origin": "https://other.example"})
                call(alice, "/auth/test/alice", 204, "POST")
                call(bob, "/auth/test/bob", 204, "POST")
                a = call(alice, "/api/member/me", 200)
                b = call(bob, "/api/member/me", 200)
                assert a["id"] != b["id"]
                assert call(alice, "/api/member/appearance", 200)["badge"] == "normal"
                call(stranger, "/api/member/chat", 401)
                defaults = call(alice, "/api/member/chat", 200)
                assert not defaults["aiConfigured"] and not defaults["settings"]["autoAccept"]
                invalid = {"welcomeEnabled": True, "welcomeMessage": "Test welcome"}
                call(alice, "/api/member/chat", 400, "PUT", invalid)
                settings = {"privateReplies": True, "privateMessage": "I am away", "groupReplies": True,
                            "groupMessages": ["Hello", "Still away", "Back later"], "autoAccept": True,
                            "welcomeEnabled": True, "welcomeMessage": "Thanks for the add", "regularGroups": ["10001"]}
                call(alice, "/api/member/chat", 204, "PUT", settings)
                assert call(alice, "/api/member/chat?id=" + b["id"], 200)["settings"]["welcomeMessage"] == "Thanks for the add"
                assert not call(bob, "/api/member/chat?id=" + a["id"], 200)["settings"]["autoAccept"]
                call(bob, "/api/member/chat", 400, "PUT", {"privateReplies": True, "privateMessage": "Test", "aiPrivate": True})
                assert call(alice, "/api/member/chat/regulars", 200)["groups"][0]["id"] == "10001"
                call(alice, "/api/member/chat/regulars/approve", 409, "POST", {"steamId": "76561198000000000"})
                call(alice, "/api/member/chat", 403, "PUT", settings, {**HEADERS, "Origin": "https://other.example"})
                assert len(admin(sock, "members", 200)["members"]) == 2
                call(alice, "/api/member/steam/link", 202, "POST")
                call(bob, "/api/member/steam/link", 409, "POST")
                time.sleep(8.5)
                assert call(alice, "/api/member/me?id=" + b["id"], 200)["linked"]
                assert not call(bob, "/api/member/me?id=" + a["id"], 200)["linked"]
                call(alice, "/api/member/steam/link", 409, "POST")
                assert len(call(alice, "/api/member/games", 200)) == 3
                call(alice, "/api/member/game", 409, "PUT", {"appId": 1})
                call(alice, "/api/member/game", 204, "PUT", {"appId": 10})
                # Duplicate starts use the same authenticated browser cookie.
                import concurrent.futures
                def attempt():
                    request = urllib.request.Request(ORIGIN + "/api/member/start", method="POST", headers=HEADERS)
                    try:
                        with alice.open(request, timeout=5) as response:
                            return response.status
                    except urllib.error.HTTPError as error:
                        return error.code
                with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
                    assert sorted(executor.map(lambda _: attempt(), range(2))) == [202, 409]
                call(alice, "/api/member/game", 204, "PUT", {"appId": 730})
                current = call(alice, "/api/member/me", 200)
                assert current["game"] == 730 and current["requested"]
                call(bob, "/api/member/steam/link", 202, "POST")
                device = {"deviceId": "2" * 32}
                admin(sock, f"members/{b['id']}/ban", 204, device)
                call(bob, "/api/member/me", 401)
                call(bob, "/auth/test/bob", 403, "POST")
                admin(sock, f"members/{a['id']}/ban", 204, device)
                call(alice, "/api/member/me", 401)
                members = admin(sock, "members", 200)["members"]
                assert all(row["banned"] and not row["linked"] and row["state"] == "banned" for row in members)
                admin(sock, "backup", 204, {})
                check_password_accounts(sock)
            finally:
                process.terminate()
                process.wait(timeout=10)
            process = start()
            try:
                call(alice, "/auth/test/alice", 403, "POST")
                admin(sock, f"members/{a['id']}/unban", 204, device)
                call(alice, "/auth/test/alice", 204, "POST")
                assert not call(alice, "/api/member/me", 200)["linked"]
                assert not call(alice, "/api/member/chat", 200)["settings"]["autoAccept"]
                assert call(alice, "/api/member/appearance", 200)["badge"] == "normal"
                assert (data / "members.backup.sqlite").stat().st_size > 0
                import sqlite3
                with contextlib.closing(sqlite3.connect(data / "members.backup.sqlite")) as backup:
                    assert backup.execute("PRAGMA integrity_check").fetchone() == ("ok",)
                    assert backup.execute("SELECT count(*) FROM members WHERE banned=1").fetchone() == (2,)
                temporary = browser()
                call(temporary, "/auth/test/key-owner", 204, "POST")
                account = call(temporary, "/api/member/me", 200)
                key_path = "/api/member/chat/key/"
                fixture_key = "gsk_" + "f" * 28
                call(stranger, key_path + "gpt", 401, "PUT", {"key": fixture_key})
                call(temporary, key_path + "gpt", 403, "PUT", {"key": fixture_key}, {**HEADERS, "Origin": "https://other.example"})
                call(temporary, key_path + "invalid", 400, "PUT", {"key": fixture_key})
                call(temporary, key_path + "gpt", 204, "PUT", {"key": fixture_key})
                call(temporary, key_path + "qwen", 204, "PUT", {"key": fixture_key})
                ai = call(temporary, "/api/member/chat", 200)
                assert ai["aiConfigured"] and ai["qwenConfigured"] and fixture_key not in str(ai)
                assert not call(alice, "/api/member/chat?id=" + account["id"], 200)["aiConfigured"]
                import sqlite3
                from contextlib import closing
                with closing(sqlite3.connect(data / "members.sqlite")) as db:
                    secrets = db.execute("SELECT secret FROM member_ai_key").fetchall()
                    assert len(secrets) == 2 and all(fixture_key not in row[0] for row in secrets)
                    assert secrets[0] != secrets[1]
                call(alice, "/api/member/chat/preview", 409, "POST", {"message": "Hi"})
                call(temporary, "/api/member/chat/preview", 400, "POST", {"message": ""})
                call(temporary, key_path + "gpt", 204, "DELETE")
                assert not call(temporary, "/api/member/chat", 200)["aiConfigured"]
            finally:
                process.terminate()
                process.wait(timeout=10)

    print("PASS member isolation, CSRF, worker limit, game switch, moderation, password signup, lockout, recovery, restart and backup")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
