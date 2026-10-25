# Contributing

Open an issue describing the problem or proposed change before a large contribution. Include the app version, operating system, installation method, and steps to reproduce. Use a separate Steam account and data directory for live testing.

1. Fork the repository and create a branch.
2. Install the .NET 10 SDK and restore `HourBoostr.Remastered.slnx`.
3. Make a focused change and run `dotnet test HourBoostr.Remastered.slnx -c Release`.
4. For dashboard changes, check both themes, keyboard controls, and small/large layouts. Public screenshots must use demo data.
5. Follow [docs/TESTING.md](docs/TESTING.md) for API/live checks. Report which checks you actually ran.
6. Open a pull request explaining the user-visible result, validation, and remaining limitations.

Never commit account configuration, pairing keys, passwords, Steam Guard codes, QR challenges, session files, logs, or real-account screenshots. See [SECURITY.md](SECURITY.md). Keep upstream credits and the GPL license intact. New dependencies need their license and purpose documented.

Reproduce public documentation screenshots with `python scripts/screenshots-demo.py`; this loopback-only preview serves illustrative data and refuses sign-in or configuration changes.
