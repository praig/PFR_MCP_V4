# PFR_MCP_V4

A cross-platform tool for Infoblox engineers servicing NIOS clients: health checks,
inventory with site locations, client engagement tracking, network diagnostics
(ping, SNMP, SSH, DNS), reporting, and remediation through NIOS CSV import files or WAPI.

Designed to run on **air-gapped** machines as self-contained executables with nothing to install.

See **[REQUIREMENTS.md](REQUIREMENTS.md)** for the full specification and open decisions.

> Status: early development. The NIOS backup parser and `pfr inspect` work; the GUI is still a shell.

## Quick start

Summarize a grid backup:

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
