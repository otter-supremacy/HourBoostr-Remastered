# Credits and project lineage

## Original HourBoostr and SingleBoostr

- **[Ezzpify](https://github.com/Ezzpify)** created the original [HourBoostr repository](https://github.com/Ezzpify/HourBoostr), including HourBoostr and SingleBoostr. The standalone Steam-client approach, original multi-account support, and original application belong to that project.
- **[rpges](https://github.com/rpges)** and **[Kasp42](https://github.com/Kasp42)** are listed as upstream contributors in the [original repository's contributor records](https://github.com/Ezzpify/HourBoostr/graphs/contributors).
- The original source and README are retained in [`legacy/`](legacy/). Upstream commit history and author attribution are preserved in Git.

This is an independent modified continuation. The original developers have not been represented as maintainers or endorsers of this remaster.

## Remaster

**Otter / [otter-supremacy](https://github.com/otter-supremacy)** maintains HourBoostr Remastered by Otter. Creator reference: [Otter’s Steam profile](https://steamcommunity.com/id/pornhub_sex5/).

The remaster replaces the runtime application with .NET 10, modern authentication, a dashboard and CLI, Windows tray controls, schedules, targets, statistics, account controls, diagnostics, tests, and release tooling. Changes are documented in [CHANGELOG.md](CHANGELOG.md).

## Dependencies and platforms

| Project | Use | License / reference |
| --- | --- | --- |
| [SteamRE / SteamKit2](https://github.com/SteamRE/SteamKit) | Steam network connectivity and authentication | LGPL-2.1; see upstream license |
| [QRCoder](https://github.com/codebude/QRCoder) | Local QR-code graphics | MIT |
| [Microsoft .NET](https://github.com/dotnet/runtime) and [ASP.NET Core](https://github.com/dotnet/aspnetcore) | Runtime, web server, and desktop hosting | Their respective license and third-party notices |
| [Microsoft Edge WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/) | Embedded Windows dashboard | Microsoft package/runtime terms |
| [xUnit.net](https://github.com/xunit/xunit) | Automated tests | Apache-2.0 |

The application is distributed under the original [GPL version 3 license](LICENSE). Dependency notices remain separate. Steam, Counter-Strike, and other game names are trademarks of their respective owners. This project is not affiliated with Valve, Steam, Oracle, or Microsoft.
