# Testing the remaster

Use a separate Steam account and an isolated data directory for live tests. The VPS installation is not a test dependency and should remain unchanged.

## Automated checks

1. Restore and run the tests:

   ```sh
   dotnet restore HourBoostr.Remastered.slnx --locked-mode
   dotnet test HourBoostr.Remastered.slnx -c Release --no-restore
   ```

2. Start the application with a dedicated test data directory:

   ```sh
   dotnet run --project src/HourBoostr.Remastered -- serve --data-dir /path/to/test-data
   ```

3. Run the API integration script against that installation:

   ```sh
   python tests/integration.py --assembly src/HourBoostr.Remastered/bin/Debug/net10.0/HourBoostr.Remastered.dll --data-dir /path/to/test-data
   ```

The integration script creates/removes only its own test profiles, verifies access controls and persistence, and does not submit real Steam credentials. It can run while another local account profile exists, but should be used only in a development installation.

## Live checks

1. Open the local dashboard, add an isolated account profile, and sign in manually.
2. Run `status` and find the opaque account ID.
3. Run the opt-in live test:

   ```sh
   python tests/live-smoke.py --assembly src/HourBoostr.Remastered/bin/Debug/net10.0/HourBoostr.Remastered.dll --data-dir /path/to/test-data --account-id ACCOUNT_ID
   ```

This checks actual owned-game selection, pause/resume, schedule pause, estimated target exclusion, random groups, and advancing statistics. It leaves the account idling its final random group.

4. Enable AFK replies, save, and send a message from an existing Steam friend. Confirm the reply arrives and an immediate second message does not trigger another reply.
5. Stop/restart the application and confirm it restores the saved session without asking for a password.
6. Test a QR sign-in separately. Confirm the browser renders a live QR challenge, scan it in Steam Mobile, and approve the sign-in. Do not post challenge screenshots or credentials to GitHub.
7. If available, open a real game with the same account in the Steam client and verify the app pauses instead of competing for the playing session.

Do not run repeated rejected-password tests against a real account. Authentication failure handling can be tested with controlled error paths; Steam rate limiting is an external behavior.

## Platform checks

- Windows: run the self-contained package without the development SDK; test a temporary current-user startup task, then remove that task.
- Linux: run the Linux package and test the user service and restart behavior on a separate machine/container.
- ARM64: building the ARM64 package validates compilation and packaging, not execution on ARM hardware.
- Docker: build and run the container where Docker is installed. Verify data survives container replacement.
- Remote HTTPS: use a certificate trusted by the test browser and verify hostname, pairing, and origin enforcement. Do not bypass browser certificate warnings.

## Diagnostic and release checks

- Confirm diagnostics and logs contain no passwords, refresh tokens, QR challenges, or chat content.
- Inspect the release archive. It must not contain `settings.json`, `*.secret`, account statistics, or local test data.
- Verify each release checksum and the update stage/apply/rollback paths using temporary managed release directories.
- Keep unverified platform paths explicitly marked as unverified before publishing.
