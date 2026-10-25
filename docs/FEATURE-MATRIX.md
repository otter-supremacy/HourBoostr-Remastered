# Feature and verification matrix

This matrix distinguishes implementation from evidence gathered on the current Windows development machine. It is updated as live checks complete.

| Feature | Implementation | Evidence / remaining verification |
| --- | --- | --- |
| Password, Steam Guard and mobile approval | SteamKit credential authentication and dashboard prompts | Different local Steam account signed in successfully |
| QR login | SteamKit QR session, locally generated SVG, refreshed challenges | Browser rendered the QR challenge; reauthentication dialog polling bug fixed and verified. Steam interrupted the challenge; user deferred scanning. Live approval remains unverified |
| Login errors and bounded retries | Result-specific errors; authentication-required state; no automatic password retry | Invalid API inputs tested; rejected credentials are not deliberately spammed against Steam |
| Protected sessions | Windows DPAPI; owner-only Linux files; no password serialization | Windows round-trip and plaintext-exclusion tests passed; live saved-session recovery after restart passed |
| Owned-game picker and random groups | Search, explicit selection, exclusion, preferred first title, random/rotation rules | Live owned-game selection and random group passed; rule tests cover ownership, count, exclusions and rotation |
| Preferred profile display | Preferred title sent first; public Steam profile presence checked separately | Profile display is controlled by Steam and is not guaranteed |
| Background recovery | Per-worker reconnection, Steam probes and watchdog; OS startup installers | Worker health rules tested; temporary Windows logon task launched the standalone package and was removed afterward; actual OS reboot and Linux service execution remain unverified |
| Pause while gaming elsewhere | Steam playing-session callback; never sends game reports while blocked | Implemented; live desktop-game overlap test pending |
| Dashboard and CLI | Authenticated local controls, dark/light theme, responsive layout, account controls | 28 API integration checks passed; all seven dashboard views checked at 375/768/1024/1440px without horizontal overflow; library search and both themes verified; no browser console warnings/errors observed |
| Windows desktop and tray | Embedded dashboard; tray icon; sign-in startup directly in tray; minimize/close-to-tray | Startup toggle, tray-only launch, minimize/restore, and three clean open/exit cycles checked; actual Windows sign-out/reboot remains unverified |
| Incoming friend requests | Opt-in per account; only individual incoming requests; spacing and duplicate retry limits | 34 unit tests include seven friend-request cases; per-account API persistence/isolation passed; completed live acceptance still requires a real incoming request |
| AFK replies | Private friends only; cooldown, quiet hours, exclusions, echo suppression | User confirmed an actual configured reply arrived |
| Schedules and targets | Time-zone/DST-aware windows and estimated-hour targets | Live schedule pause and target exclusion passed; overnight/DST tests passed |
| Statistics | Persistent per-game estimates and separate Steam-reported minutes | Counters advanced in live idling test; persistence covered by storage tests |
| Multiple accounts | Separate workers, vault files, settings and statistics | Isolation API/storage tests passed; simultaneous multiple live accounts not yet tested |
| Windows/Linux/ARM64 packages and Docker | Self-contained publish scripts, checksums, installers and container recipes | All three packages built and passed checksum, license, private-data and launcher checks. Windows standalone startup tested. Native Linux/ARM64 and Docker execution remain unverified |
| Diagnostics | Redacted doctor command and downloadable dashboard report | Identity/credential redaction tests and real API checks passed |
| Updates and releases | Explicit source, release ordering, checksummed staging, managed apply/rollback, CI recipes | Checksum, zip traversal, apply and rollback tests passed; hosted GitHub workflows have not run |

Hosted CI results are reported on the repository's Actions page. Update the evidence when those runs complete; do not represent unverified platform or account paths as tested. See [WINDOWS-DESKTOP.md](WINDOWS-DESKTOP.md), [FRIEND-REQUESTS.md](FRIEND-REQUESTS.md), and the dated local verification notes for details.
