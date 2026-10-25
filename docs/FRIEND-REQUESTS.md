# Automatic friend-request acceptance

Enable **Settings → Automatically accept incoming friend requests → Save account** for each account that should accept requests. The setting defaults to off for new profiles. It has been enabled for the existing local test profile and the separate VPS account at the user's request.

The app collects incoming requests from Steam's initial friend list and incremental updates. Before each acceptance it checks the current relationship again. It accepts only `EFriendRelationship.RequestRecipient` entries for `EAccountType.Individual`, excludes the account itself, spaces attempts by at least two seconds, and waits ten minutes before retrying the same unresolved request. Blocked, ignored, existing-friend, outgoing-request and group entries do not qualify.

Successful acceptance is recorded only after Steam returns `EResult.OK` in `FriendAddedCallback`. Activity logs omit the other user's name and Steam ID. Steam errors are recorded without automatically resending immediately.

Verification on 2026-10-26:

- 34 unit tests passed, including seven new friend-request cases.
- 28 API integration checks passed, including persistence and isolation of the new setting.
- The local API persisted the enabled setting.
- The VPS build succeeded, the enabled service restarted and reconnected, and startup logs confirmed auto-accept enabled. Its ten configured games, Counter-Strike preference, Dota 2 selection, and AFK configuration were retained.
- No new request was sent on the user's behalf. A real incoming request is still needed to verify acceptance end to end.

Reference: [SteamKit maintainer example](https://github.com/SteamRE/SteamKit/discussions/1485).
