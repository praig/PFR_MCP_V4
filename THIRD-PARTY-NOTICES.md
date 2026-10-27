# Third-party notices

This file lists every third-party component shipped with PFR_MCP_V4, with its
license, for client security reviews. Update it whenever a dependency is added,
removed, or upgraded.

## Shipped in the application

| Component | Version | License | Used by | Source |
|---|---|---|---|---|
| .NET runtime | 8.x | MIT | All | https://github.com/dotnet/runtime |
| Avalonia | 11.3.0 | MIT | GUI | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Desktop | 11.3.0 | MIT | GUI | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Themes.Fluent | 11.3.0 | MIT | GUI | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 11.3.0 | MIT (code); Inter font under SIL OFL 1.1 | GUI | https://github.com/AvaloniaUI/Avalonia |

## Build and test only (not shipped)

| Component | Version | License |
|---|---|---|
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache 2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache 2.0 |

## Planned (not yet referenced)

| Component | License | Purpose |
|---|---|---|
| Avalonia.Controls.DataGrid | MIT | Inventory tables |
| ScottPlot or LiveCharts2 | MIT | Charts |
| SharpSnmpLib | MIT | SNMP |
| SSH.NET | MIT | SSH |
| DnsClient.NET | Apache 2.0 | DNS lookups |
