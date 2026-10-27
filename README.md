# PFR_MCP_V4

A cross-platform tool for Infoblox engineers servicing NIOS clients: health checks,
inventory with site locations, client engagement tracking, network diagnostics
(ping, SNMP, SSH, DNS), reporting, and remediation through NIOS CSV import files or WAPI.

Designed to run on **air-gapped** machines as self-contained executables with nothing to install.

See **[REQUIREMENTS.md](REQUIREMENTS.md)** for the full specification and open decisions.

> Status: early development. Inventory and encrypted client workspaces (clients → grids → snapshots) work in the GUI and CLI.

## Quick start

Open the desktop app and click **Open backup…** (or pass the file on the command line):

```bash
dotnet run --project src/Pfr.Gui
dotnet run --project src/Pfr.Gui -- path/to/grid-backup.bak
```

The Inventory module shows an overview plus searchable, sortable tables of members, DNS views,
zones, networks, DHCP ranges, DHCP failover and DNS records. Search matches every word you type
across all columns; rows can be multi-selected and copied (Ctrl+C) with headers.

### Client data

Client workspaces (contacts, notes, engagement log, and each client's grids with dated snapshots)
are kept **encrypted** in a `PfrData` folder next to the application, unlocked with a master
password. Snapshots store the parsed inventory only (no secrets), so switching between clients and
grids is instant and never needs the original backup. Backups without a grid name are refused.

```bash
pfr init                                            # create the encrypted data folder
pfr client-add "Acme Corp"
pfr import --client "Acme Corp" grid-backup.bak --label "Q4 review"
pfr clients                                         # clients and their grids
pfr show --client "Acme Corp" --grid "Production"   # latest snapshot, no backup needed
```

Set `PFR_MASTER_PASSWORD` for scripted use, and `PFR_DATA_DIR` (or `--data`) to use another folder.

Summarize a grid backup from the command line:

```bash
dotnet run --project src/Pfr.Cli -- inspect path/to/grid-backup.bak
dotnet run --project src/Pfr.Cli -- inspect path/to/grid-backup.bak --json > inventory.json
```

The parser reads only the object types it needs and drops secrets (keys, passwords, tokens)
before anything else sees them.

## Layout

```
PFR_MCP_V4.sln
src/
  Pfr.Core/    Shared engine (parsers, storage, rules, vault, network tools, reporting, WAPI)
  Pfr.Cli/     Command-line app  -> executable "pfr"
  Pfr.Gui/     Avalonia desktop app -> executable "PFR"
tests/
  Pfr.Core.Tests/   xUnit tests for the core
  Pfr.Gui.Tests/    Headless Avalonia UI tests (set PFR_SCREENSHOT_DIR to save screenshots)
```

## Prerequisites (development machine only)

- .NET SDK 8.0 or later — https://dotnet.microsoft.com/download
- Internet access to `api.nuget.org` for the first package restore

End users do **not** need .NET installed; published builds are self-contained.

## Build and test

```bash
dotnet restore
dotnet build
dotnet test
```

To also test against a real backup (kept in the git-ignored `samples/private/` folder):

```bash
# PowerShell
$env:PFR_SAMPLE_BACKUP = "samples\private\my-grid.bak"; dotnet test
# bash
PFR_SAMPLE_BACKUP=samples/private/my-grid.bak dotnet test
```

Run during development:

```bash
dotnet run --project src/Pfr.Cli -- help
dotnet run --project src/Pfr.Gui
```

## Publish self-contained single-file builds

Pick a runtime identifier: `win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`.

```bash
# CLI
dotnet publish src/Pfr.Cli -c Release -r linux-x64 --self-contained true -o publish/cli-linux-x64

# GUI
dotnet publish src/Pfr.Gui -c Release -r win-x64   --self-contained true -o publish/gui-win-x64
dotnet publish src/Pfr.Gui -c Release -r osx-arm64 --self-contained true -o publish/gui-osx-arm64
```

The output folder contains a single executable plus, for the GUI, a few native
libraries. Copy it to the target machine and run it — no installation required.

## Data handling

- Client data and credentials stay on the local machine.
- Never commit client backups, exports, workspaces, or vault files; `.gitignore` blocks the common ones.

## Licensing

Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
