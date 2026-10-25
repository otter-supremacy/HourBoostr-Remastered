# Security and private data

Do not post passwords, Steam Guard codes, saved session files, dashboard pairing keys, QR challenges, or real account configuration in an issue or pull request.

The dashboard binds to loopback by default. Remote mode requires explicit opt-in, a configured hostname, and HTTPS. API data requires a dashboard session or bearer key. Mutations require a custom request header, and foreign browser origins are rejected.

Windows sessions are encrypted using DPAPI for the current Windows identity. Linux session files are owner-only and are not encrypted at rest. Protect the machine, account, and backups accordingly. No recovery key or session data is sent to a project-operated server.

Friend-request acceptance is off by default and can be enabled per account. It accepts only incoming requests from individual accounts, rechecks the current relationship, spaces attempts, and limits duplicate retries. The app does not trade items, read chat history, or log chat message bodies. AFK replies operate only on incoming private messages from existing friends after the user enables the feature.

Refresh-token rejection pauses sign-in attempts. Network failures reconnect with backoff. The project makes no guarantee about Steam account restrictions, hosting availability, playtime credit, or the behavior of external services.

Before publishing a security report, reproduce it with a test account and a redacted diagnostic report. A private reporting channel can be configured in the future GitHub repository; do not invent or assume a maintainer contact address.
