# Local verification, 2026-10-25

The remaster was tested on Windows with an isolated data directory and a different Steam account. These checks were performed before publication and did not require production VPS changes. Later desktop/friend-request changes have their own dated verification notes.

## Verified

- Build succeeded with zero warnings and errors.
- 27 unit tests passed, including game-selection rules, schedules/DST, AFK gating, storage, dashboard authentication, diagnostics redaction, and update staging/apply/rollback.
- 27 API integration checks passed on the final running build, including access controls, request-origin checks, input validation, settings persistence, account isolation, and diagnostic redaction.
- A real Steam account signed in with password/Steam Guard. Saved-session recovery succeeded after restarting the app.
- Live owned-game selection, pause/resume, schedule pause, estimated-hour target exclusion, random groups of ten, and advancing statistics passed.
- The user confirmed that the configured AFK reply arrived in Steam.
- A QR challenge rendered in the browser. Testing exposed a dialog polling bug for already-connected accounts; the fix kept the dialog open across subsequent polling. Steam interrupted the challenge before a completed QR sign-in could be verified; the user deferred scanning. The saved local session was restored afterward.
- All seven dashboard views were checked at 375, 768, 1024, and 1440 pixels without horizontal overflow. Library search returned the expected game. Both themes rendered, and no console warnings or errors were observed.
- A temporary Windows logon task launched the standalone Windows package on a separate test port. The task was stopped and removed afterward. No actual Windows reboot was performed.
- Self-contained Windows x64, Linux x64, and Linux ARM64 archives built successfully. Checksums, GPL license/attribution files, platform launcher headers, Linux executable permissions, and exclusion of private runtime files were checked.
- The dependency audit reported no known vulnerable packages in the configured feed at the time of testing.
- Source, tests, scripts, and documentation were checked for the local account identity and production VPS address; neither was found.

## Still unverified

- Completed QR approval in Steam Mobile.
- Playing-elsewhere behavior while a real Steam desktop game is running.
- Multiple live Steam accounts in the same remaster process.
- Native Linux and ARM64 execution, Linux service startup, Docker execution, and remote HTTPS with a trusted certificate.
- Hosted GitHub CI/release workflows and an actual reboot with startup enabled.

The current build is a preview. These checks do not establish uninterrupted operation or guarantee Steam playtime accounting or profile display.
