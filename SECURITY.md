# Security and private data

Do not post passwords, Steam Guard codes, saved session files, dashboard pairing keys, QR challenges, or real account configuration in an issue or pull request.

The dashboard binds to loopback by default. Remote mode requires explicit opt-in, a configured hostname, and HTTPS. API data requires a dashboard session or bearer key. Mutations require a custom request header, and foreign browser origins are rejected.

Windows sessions are encrypted using DPAPI for the current Windows identity. Linux session files are owner-only and are not encrypted at rest. Protect the machine, account, and backups accordingly. No recovery key or session data is sent to a project-operated server.

Friend-request acceptance is off by default and can be enabled per account. It accepts only incoming requests from individual accounts, rechecks the current relationship, spaces attempts, and limits duplicate retries. Group Regulars requires explicit opt-in from the other person and manual approval of each request. Activity logs exclude chat and comment bodies.

When enabled, Groq receives eligible message or profile comment text for reply generation or screening. Profile comment drafts and Group Regulars activity metadata are kept in the account’s local data directory. Protect that directory and its backups. Profile comment replies are posted manually in Steam. See the [chat and profile tools guide](docs/CHAT-AND-PROFILE.md).

Refresh-token rejection pauses sign-in attempts. Network failures reconnect with backoff. The project makes no guarantee about Steam account restrictions, hosting availability, playtime credit, or the behavior of external services.

Before publishing a security report, reproduce it with a test account and a redacted diagnostic report. A private reporting channel can be configured in the future GitHub repository; do not invent or assume a maintainer contact address.
