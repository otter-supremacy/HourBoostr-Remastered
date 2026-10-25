# Windows desktop changes, 2026-10-26

- Display name: **HourBoostr Remastered by Otter**. Existing data-directory names and executable identifiers remain compatible.
- Creator reference: [Otter’s Steam profile](https://steamcommunity.com/id/pornhub_sex5/), in Settings, the desktop menu, README, and NOTICE.
- `HourBoostr.Desktop.exe` embeds the dashboard using Microsoft Edge WebView2 and manages a hidden Steam worker process.
- **Start with Windows** creates a current-user sign-in entry with `--start-minimized`. No dashboard or console window is opened during that startup path.
- Minimizing hides the window in the tray. Closing also hides it when **Close window to tray** is enabled, which is the default. **Exit** stops the dashboard and Steam workers.
- The tray icon restores the window. Single-instance handling prevents duplicate desktop hosts for the same data directory.
- Shutdown cancels pending dashboard work without displaying expected `E_ABORT` or disposal errors. Cleanup is idempotent, and asynchronous work uses a captured cancellation token.

## Verification

- Desktop build: zero warnings/errors.
- 27 existing unit tests passed after the branding and desktop changes.
- Tested tray-only startup with the worker ready, startup enable/disable/re-enable, minimize, and restore through the launcher's current-user control interface.
- Three separate open-and-exit cycles passed on an isolated empty profile, including a minimize/restore cycle and exits while the embedded dashboard was initializing. Each desktop process returned success without blocking on a popup, and its worker stopped.
- Native accessibility exposed the renamed window, the dashboard, and the saved local account reporting ten games. Native screenshot/input automation was unavailable in this Windows session, so a full visual check of the native controls was not completed.
- Actual Windows sign-out/reboot remains untested. The sign-in entry and its minimized launch behavior were verified separately.

Microsoft reference: [WebView2 in WinForms](https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/winforms).
