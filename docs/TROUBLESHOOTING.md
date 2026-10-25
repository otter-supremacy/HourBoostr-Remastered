# Troubleshooting and FAQ

## Steam login does not work

1. Check the Steam account login name. Your public profile nickname and custom Steam URL are not necessarily the login name.
2. Confirm password/Guard entry directly in the local app. Do not paste credentials into GitHub issues or terminal command arguments.
3. Read the app's sign-in state. Rejected/expired credentials require a new sign-in; repeated password submissions are not retried automatically.
4. If Steam reports a rate limit, wait before retrying. If Steam is unavailable, reconnect later.
5. If a remembered session is corrupt or rejected, use **Forget saved session**, then sign in again. Keep statistics/settings backups if you need them.

The password/Guard and saved-session paths were tested locally. QR rendering is implemented; completed QR approval remains unverified in this preview. Use password/Guard when QR is unavailable.

## Dashboard asks for a pairing key

Use the worker's `open` command with the same data directory and port, or obtain `dashboard-key` locally and enter it in that installation's browser. The Windows desktop window pairs its embedded browser automatically. Pairing keys are credentials; never put them in public posts.

## The app is running but I cannot see a window

The Windows startup path deliberately opens directly in the tray. Look in Windows' hidden-icons menu and double-click the HourBoostr icon. **Minimize** and, by default, **Close** keep workers running. Use the tray's **Exit** command to stop them.

## Startup does not work

The Windows switch starts at this user's Windows sign-in. It cannot keep a powered-off or sleeping machine online. Confirm startup is enabled, the extracted folder still exists at the registered path, and Windows has not disabled the entry. Toggle startup off/on after moving the folder. On Linux, confirm the user service is enabled and lingering is configured if the service must run without login.

## My game library is empty

Confirm the account is signed in, then select **Refresh library**. Only the authenticated account's owned games are eligible. The remaster does not purchase or automatically acquire games. A successful library request can return a different set from what is visible on a public profile.

## Games are not being reported as playing

Check whether the account is disabled, manually paused, outside its schedule, at its estimated targets, or playing elsewhere. Select eligible owned games and save the selection. Check **Overview** and Diagnostics. A connected Steam session alone does not mean any games have been selected.

## My profile shows the wrong game or says offline

Set a preferred profile game and save. The app sends that game first, but Steam chooses public display and public profile data can lag. Privacy settings also affect what others can see. The app does not guarantee a particular displayed game.

## Local hours and Steam hours differ

Local counters are estimates. Steam-reported minutes come from a separate library response and can update later. Combined game-hours count several simultaneous games separately. Estimated targets do not guarantee Steam credited the same amount.

## AFK replies do not arrive

1. Enable replies and select **Save replies**.
2. Test using a new private message from an existing Steam friend.
3. Check cooldown, excluded friends, quiet hours, and connection state.
4. Look for a successful send or Steam result in activity logs. Group messages, self-messages, and local echoes do not qualify.

## Friend requests are not accepted

Enable **Automatically accept incoming friend requests** in Account settings and save. Only pending/new incoming requests from individual accounts qualify. A blocked user, group, existing friend, or outgoing request does not qualify. Attempts are spaced by two seconds, with a ten-minute cooldown before retrying the same unresolved request. Steam can reject acceptance; check activity logs. Live acceptance still needs validation in the preview's feature matrix.

## Port already in use / second installation fails

Stop the installation using that port or choose a different `--port`. Use a different `--data-dir` for an independent installation. Keep those values consistent across `serve`, `open`, `status`, `stop`, and desktop commands. Do not run the same Steam account from two idlers while testing.

## Linux reports missing libraries or cannot execute

Confirm the package architecture matches `uname -m`, the executable has permission (`chmod u+x HourBoostr.Remastered`), and your distribution meets [.NET's Linux prerequisites](https://learn.microsoft.com/en-us/dotnet/core/install/linux). Self-contained means .NET is bundled; it does not bundle every operating-system library. Native Linux/ARM64 execution remains unverified for this preview.

## Is it free? Will it run 24/7?

The source is distributed under GPL-3.0. The app has no subscription/payment integration. Your computer, connectivity, electricity, and hosting can have costs. The application does not create cloud resources or promise a free hosting plan. It can run while its machine, network, and Steam session remain available; recovery features are not an uptime guarantee.

## Is this the original HourBoostr? Does it farm cards?

This is Otter's independent continuation of Ezzpify's project. The original source is preserved in `legacy/`, with contributor credits and commit history. The new runtime is a standalone Steam-session idler. SingleBoostr's card-farming functionality is not implemented here. It is not an official Valve or upstream-maintainer release.

## How do I report a problem?

Use the repository's bug-report template. Include app version, platform/architecture, setup method, expected result, actual result, and reproduction steps. Review diagnostics before attaching them. Never attach account settings, sessions, pairing keys, QR challenges, passwords, Guard codes, or private chats. See [SECURITY.md](../SECURITY.md) for sensitive reports.
