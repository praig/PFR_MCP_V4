# Third-party notices

This file lists every third-party component shipped with PFR_MCP_V4, with its
license, for client security reviews. Update it whenever a dependency is added,
removed, or upgraded (`dotnet list src/Pfr.Gui package --include-transitive`).

## Shipped in the application

| Component | Version | License | Used by | Source |
|---|---|---|---|---|
| .NET runtime | 8.x | MIT | All | https://github.com/dotnet/runtime |
| Avalonia (core, Desktop, Themes.Fluent, Skia, Win32, X11, Native, FreeDesktop, Remote.Protocol) | 11.3.0 | MIT | GUI | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Controls.DataGrid | 11.3.0 | MIT | GUI | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 11.3.0 | MIT (code); Inter font under SIL OFL 1.1 | GUI | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Angle.Windows.Natives | 2.1.22045 | BSD-3-Clause (ANGLE Project) | GUI (Windows) | https://github.com/AvaloniaUI/angle |
| SkiaSharp (+ native assets) | 2.88.9 | MIT | GUI | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp (+ native assets) | 7.3.0.3 | MIT | GUI | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime | 0.11.0 | MIT | GUI | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | 0.21.2 | MIT | GUI (Linux) | https://github.com/tmds/Tmds.DBus |
| System.IO.Pipelines | 8.0.0 | MIT | GUI | https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite (with Microsoft.Data.Sqlite.Core) | 8.0.11 | MIT | GUI, CLI (Explore data) | https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, and the native e_sqlite3 library for each OS) | 2.1.x (confirm with `dotnet list package --include-transitive`) | Apache 2.0 | GUI, CLI (Explore data) | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (inside e_sqlite3) | bundled | Public domain | GUI, CLI (Explore data) | https://www.sqlite.org/copyright.html |

The CLI (`pfr`) uses the .NET runtime and the SQLite packages above (for `pfr query`); nothing else.
SQLite runs in memory only: Explore data never writes a database file.

## Build and test only (not shipped)

| Component | Version | License | Note |
|---|---|---|---|
| Avalonia.BuildServices | 0.0.31 | MIT | Build-time statistics collector pulled in by Avalonia; **disabled** in `Directory.Build.targets` |
| Avalonia.Headless.XUnit | 11.3.0 | MIT | GUI tests |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT | |
| xunit | 2.9.2 | Apache 2.0 | |
| xunit.runner.visualstudio | 2.8.2 | Apache 2.0 | |

## Planned (not yet referenced)

| Component | License | Purpose |
|---|---|---|
| ScottPlot or LiveCharts2 | MIT | Charts |
| SharpSnmpLib | MIT | SNMP |
| SSH.NET | MIT | SSH |
| DnsClient.NET | Apache 2.0 | DNS lookups |
