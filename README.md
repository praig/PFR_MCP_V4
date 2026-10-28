# PFR_MCP_V4

A cross-platform tool for Infoblox engineers servicing NIOS clients: health checks,
inventory with site locations, client engagement tracking, network diagnostics
(ping, SNMP, SSH, DNS), reporting, and remediation through NIOS CSV import files or WAPI.

Designed to run on **air-gapped** machines as self-contained executables with nothing to install.

See **[REQUIREMENTS.md](REQUIREMENTS.md)** for the full specification and open decisions.

> Status: early development. Inventory, encrypted client workspaces (clients → grids → snapshots) and the first reports work.

## Quick start

Open the desktop app and click **Open backup…** (or pass the file on the command line):

```bash
dotnet run --project src/Pfr.Gui
dotnet run --project src/Pfr.Gui -- path/to/grid-backup.bak
```

The Inventory module shows an overview plus searchable, sortable tables of members, DNS views,
zones, networks, DHCP ranges, DHCP failover and DNS records. Search matches every word you type
across all columns; rows can be multi-selected and copied (Ctrl+C) with headers.

### Reports

The Reports tab runs on whichever grid is open: **License expiry** (expired and soon-to-expire
licenses, with a selectable window) and **Dangling CNAMEs** (CNAME chains followed through the
grid's own zones, flagging missing, address-less, disabled and looping targets plus possible
subdomain-takeover risks). *Explore data* runs read-only SQL against the grid's snapshot (see below).

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

### Grid connections

Each grid can also keep its WAPI connection — Grid Master address, port, optional WAPI version, and a
read-only and an optional read-write account — encrypted with the same master password and stored with the
client. In the desktop app it is the **Connection** tab next to a grid's snapshots (Clients › client › Grids).
Passwords are masked unless you tick *Show passwords*, and are never written to snapshots or reports.

```bash
pfr connection-set --client "Acme Corp" --grid "Production" --address gm.acme.example --ro-user pfr-ro
pfr connection-set --client "Acme Corp" --grid "Production" --rw-user pfr-rw   # add the optional read-write account
pfr connection-show --client "Acme Corp" --grid "Production"                   # passwords hidden (--reveal to show)
pfr connection-remove --client "Acme Corp" --grid "Production"
```

Passwords are never given on the command line: they are prompted without echo, or read from
`PFR_RO_PASSWORD` / `PFR_RW_PASSWORD` for scripted use.

### Client site logins and API keys

A client can also keep logins and API keys for **its own sites and services** (portals, VPNs, cloud consoles,
ticketing): a title, site, user id, password, API key and notes, any of the secrets optional. They are encrypted with
the same master password and stored with the client; in the desktop app they are on the client page under
**Logins & keys**, masked until you tick *Show*.

```bash
pfr login-set --client "Acme Corp" --title "Acme VPN portal" --url https://vpn.acme.example --user pat --password
pfr login-set --client "Acme Corp" --title "Cloud API" --api-key            # an API-key-only entry
pfr logins --client "Acme Corp"                                              # list; no secrets shown
pfr login-show --client "Acme Corp" --title "Cloud API" [--reveal]
pfr login-remove --client "Acme Corp" --title "Cloud API"
```

`--password` and `--api-key` prompt without echo (or read `PFR_LOGIN_PASSWORD` / `PFR_LOGIN_API_KEY`); secrets are
never accepted as arguments.

### Pulling a backup over WAPI

With a grid's connection saved (address, read-only account), the app can reach the grid's WAPI directly. The grid's
TLS certificate is **pinned** on first connect: you are shown its SHA-256 fingerprint, compare it with the grid's own
certificate, and trust it once. After that the connection is dropped, before any user id or password is sent, if the
certificate ever changes. Everything the client does is read-only.

In the desktop app use **Test connection** and **Pull backup now** on the grid's Connection tab. From the command line:

```bash
pfr wapi-test   --client "Acme Corp" --grid "Production"      # shows the fingerprint to trust on first use
pfr wapi-backup --client "Acme Corp" --grid "Production" --label "Before upgrade"
```

A pulled backup is parsed as it downloads and is **never written to disk**; only the parsed inventory is kept, as a
snapshot of the grid. The read-only account needs permission to take backups on the grid. For scripts, pass the
fingerprint you have verified with `--pin <sha256>`.

### Live status over WAPI

The grid's **Live status** tab (and the command line) reads what the grid reports right now: each member and its
nodes, HA state (and whether an HA pair has exactly one active node), every service's state, NIOS versions and
hotfixes, rolled up into an overall status with a list of problems. It is read-only, uses the same pinned certificate
and read-only account, and stores nothing.

```bash
pfr wapi-status --client "Acme Corp" --grid "Production"           # table plus problems
pfr wapi-status --client "Acme Corp" --grid "Production" --json
pfr wapi-get --client "Acme Corp" --grid "Production" --object member --fields host_name,node_info
```

The grid is asked which fields each object offers and only those are requested, so a field your NIOS or WAPI version
lacks shows up as a note instead of an error. `wapi-get` prints the grid's raw answer to one read request (or an
object's schema with `--schema`), which is the quickest way to check field names against your grid.

### Explore data (SQL on a snapshot)

Reports › **Explore data** lets you query the open grid's stored snapshot with SQL: no backup needed, nothing
written anywhere, and the snapshot can't be changed (one `SELECT` at a time, on a throwaway in-memory copy).
A table browser shows what you can query (`members`, `nodes`, `member_services`, `licenses`, `dns_views`, `zones`,
`zone_servers`, `networks`, `dhcp_ranges`, `failover`, `records`...), with every column described, and a drop-down
of example queries gets you started. Press **Ctrl+Enter** to run; results are searchable, sortable and copyable.

SQL has no idea how addresses sort, so there are three helpers: `ip_key(x)` (order addresses and prefixes
numerically), `cidr_contains(prefix, x)` and `ip_in_range(x, start, end)`.

```sql
-- DHCP ranges and who serves them, in address order
SELECT m.host_name, r.start_address, r.end_address, r.association_type
FROM dhcp_ranges r LEFT JOIN members m ON m.oid = r.member_oid
ORDER BY m.host_name, ip_key(r.start_address);

-- Every A record inside 10.0.0.0/8
SELECT fqdn, value FROM records WHERE type = 'A' AND cidr_contains('10.0.0.0/8', value) ORDER BY ip_key(value);
```

From the command line:

```bash
pfr tables --client "Acme Corp" --grid "Production"
pfr query  --client "Acme Corp" --grid "Production" "SELECT type, COUNT(*) FROM records GROUP BY type"
pfr query  --file backup.bak --csv "SELECT fqdn, value FROM records WHERE type = 'A'" > a-records.csv
```

### Report templates (saved queries)

Any Explore data query can be kept as a **report template**: *Save as report…* gives it a name and a description,
and it then appears under **My reports** in the Reports list. Picking it runs it on whichever grid is open, so a
query you write once can be re-run on any client's grid. *Edit* opens it back in Explore data (save to update it,
rename it, or save a copy) and *Delete report* removes it.

Templates are plain, readable JSON files, one per report, in a `ReportTemplates` folder next to the app (set
`PFR_TEMPLATES_DIR`, or `--templates <folder>` on the command line, to keep them elsewhere):

```json
{
  "format": 1,
  "name": "Networks with no DHCP range",
  "description": "Leaf networks that nobody serves.",
  "sql": [
    "SELECT n.prefix, n.comment",
    "FROM networks n LEFT JOIN dhcp_ranges r ON r.network_key = n.network_key",
    "WHERE n.is_container = 0 AND r.network_key IS NULL",
    "ORDER BY ip_key(n.prefix)"
  ]
}
```

Because they are just files, you can copy them to a colleague, keep them in version control, or write them in a text
editor (then press *Reload*). They are **not encrypted** and need no master password, so keep client names and
anything sensitive out of them. A file whose SQL isn't a single read-only query is never run: it is skipped and
listed as a problem instead.

```bash
pfr report-list
pfr report-save --name "A records in 10/8" --description "Every A record in 10.0.0.0/8" \
    "SELECT fqdn, value FROM records WHERE type = 'A' AND cidr_contains('10.0.0.0/8', value) ORDER BY ip_key(value)"
pfr report-run  --name "A records in 10/8" --client "Acme Corp" --grid "Production" --csv > a-records.csv
pfr report-delete --name "A records in 10/8"
```

### Parameters and exporting

Put `:name` in the SQL wherever a value should be asked for, and Explore data shows an input box for it. The value
is handed to the query as data (never pasted into the SQL), so what is typed can't change what the query does; a blank
value is NULL, which lets a filter be optional:

```sql
SELECT fqdn, value FROM records
WHERE type = :type AND cidr_contains(:network, value) AND (:zone IS NULL OR zone_key LIKE :zone)
ORDER BY ip_key(value);
```

*Save as report…* turns each `:name` into a declared parameter of the report. A small editor lets you give it a
label, a description, a type (text, number, or a choice from a list of options), a default, and whether it is
required. Open the report under My reports and it shows a form with the defaults filled in; it runs by itself when
every required value is there, and otherwise waits for you to fill them in and click Run. In the file the
parameters look like this:

```json
"parameters": [
  { "name": "type", "label": "Record type", "type": "choice", "options": ["A", "AAAA"], "default": "A" },
  { "name": "network", "label": "Network", "description": "Only records inside it.", "default": "10.0.0.0/8" },
  { "name": "zone", "label": "Zone key contains", "required": false }
]
```

**Export…** (on Explore data and on every report) saves the results as CSV or JSON, whichever the file name ends
in. CSV files open correctly in Excel (accents included), and text that starts with `=`, `+`, `-` or `@` gets a
leading apostrophe, because DNS names and record data come from outside and a spreadsheet would otherwise run such a
cell as a formula. On the command line:

```bash
pfr report-run --name "A in a network" --client "Acme Corp" --grid "Production" \
    --param network=10.20.0.0/16 --param type=A --out a-records.csv --excel-safe
pfr query --file backup.bak --param host=gm.example.com --json \
    "SELECT * FROM members WHERE host_name = :host"
```

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
