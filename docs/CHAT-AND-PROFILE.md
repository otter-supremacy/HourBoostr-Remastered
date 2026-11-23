# Chat and profile tools

Open **AFK replies** for the selected Steam account. Group replies, Group Regulars, Groq options, and profile comment monitoring each have their own controls.

- **Group replies** rotate three saved messages per room. Set the interval between replies before enabling the feature.
- **Friend welcomes** are configured in **Settings**. A welcome is sent after HourBoostr automatically accepts an incoming friend request. A delivery failure does not trigger a retry.
- **Group Regulars** watches only the groups you select. A person qualifies after activity on three separate UTC dates within 14 days in the same group and submitting the opt-in command shown in the dashboard. Each request needs your confirmation. One request per account can be sent through the feature in 24 hours.
- **Profile comment inbox** checks Steam every 30 seconds while the account is connected. It ignores old comments, filters detected links locally, and asks Groq to screen eligible text. The private inbox lets you copy exact text and open the author’s profile. Review the text before posting it in Steam.
- **Profile display** is in **Game library**. Choose a rotation or custom status there. Steam controls which activity other users see.

## Steam Friends appearance

1. Select an account and open **Settings → Appearance**.
2. Pick a Friends status and display badge. Choose **Normal** to clear the badge. **Invisible** requests an offline appearance and saves the chosen badge for when you become visible again.
3. Select **Save account**. Use **Edit custom status** to open **Game library → Profile display** and set the custom non-Steam title.

**Play Together**, **Mobile**, **Big Picture**, and **VR** request display flags. Steam and the viewing client decide what appears. The appearance preview shows the local request. This feature is available in the current source build. The hosted Members demo uses simulated Steam. [Custom Steam status guide](https://hourboostr.pages.dev/guides/custom-steam-status/).

Groq is optional and off for chat replies by default. Configure it on the machine running HourBoostr with the `ai-key` command, which reads the value from standard input. Use the same data directory as the running instance. The dashboard displays connection status and a preview that does not send a Steam message. Eligible text goes to Groq when you enable the relevant feature. The browser does not receive the credential.

Profile comment checks share the configured account and global Groq request budgets with chat replies. If screening is unavailable, eligible text appears as **Needs review**. The inbox shows up to 40 recent entries for seven days. **Pause and clear inbox** removes the saved entries and stops monitoring.
