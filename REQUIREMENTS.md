# PFR_MCP_V4 — Requirements

Status: **Draft v0.3** — updated 2026-10-27. Items marked **[PENDING]** are open decisions (see [Open decisions](#14-open-decisions)).
Items marked **✅** are implemented.

---

## 1. Purpose

A tool for an Infoblox engineer servicing multiple clients. It:

- inventories NIOS grids from backups, CSV exports or WAPI, with location context,
- tracks each client engagement over time,
- audits grids and reports health-check findings,
- provides network diagnostic tools (ping, SNMP, SSH, DNS),
- produces client-ready reports, and
- remediates issues through generated NIOS CSV import files or directly over WAPI (when read-write mode is enabled).

Scope is **NIOS (on-prem grids)**. Forward Networks integration is **deferred** (see §13).

---

## 2. Platform and technology

| Item | Decision |
|---|---|
| Language / runtime | C# on .NET 8 or later ✅ |
| Desktop GUI | Avalonia 11 (MIT core only), **dark theme** ✅ — Windows and macOS (Linux desktop also works) |
| CLI | `pfr` — Linux; also usable on Windows and macOS ✅ |
| Architecture | One shared core library (`Pfr.Core`) used by GUI and CLI ✅ |
| Third-party components | Free/permissive licenses only (MIT, Apache 2.0, BSD). No paid Avalonia tiers or commercial libraries ✅ |
| Cryptography | .NET built-in cryptography only |
| License record | `THIRD-PARTY-NOTICES.md` lists every shipped dependency ✅ |
| Source control | Private GitHub repository `praig/PFR_MCP_V4` ✅ |
| Tests | xUnit for the core; headless Avalonia UI tests that can save screenshots ✅ |
| Extensibility | Modular so new modules (e.g. Forward Networks) can be added without reworking the core |

### 2.1 Why C#
- Self-contained single-file executables, cross-compiled for Windows, macOS and Linux from one machine.
- Nothing to install on target machines — suits air-gapped client environments (Python rejected: library dependencies are hard to satisfy air-gapped; packaged Python binaries are large and often flagged by AV/EDR).
- Avalonia provides a cross-platform desktop GUI under MIT.

### 2.2 Libraries (all free/permissive)
| Need | Library | License | Status |
|---|---|---|---|
| GUI | Avalonia, Avalonia.Controls.DataGrid | MIT | ✅ |
| Charts | ScottPlot or LiveCharts2 | MIT | planned |
| SNMP | SharpSnmpLib | MIT | planned |
| SSH | SSH.NET | MIT | planned |
| DNS | DnsClient.NET | Apache 2.0 | planned |
| Ping | System.Net.NetworkInformation (built in) | — | planned |

---

## 3. Air-gapped and offline operation

- Runs on air-gapped machines with nothing to install.
- Distributed as **self-contained, single-file executables** per OS.
- No license keys, activation, telemetry, or update checks — **including at build time** (Avalonia's build statistics collector is disabled) ✅.
- The **only** network traffic is user-initiated, to targets the user specifies: WAPI, ping, SNMP, SSH, and DNS queries.
- All client data and credentials stay on the local machine.
- Health-check rules can be added or edited with a text editor — no recompiling.

---

## 4. Inputs

1. **Grid backup** (`.bak` / `.tar.gz` / `.tar` containing `onedb.xml`, or a bare `onedb.xml`) — **primary source** ✅.
   - Streamed in place; never extracted to disk ✅.
   - Grid backups contain secrets (private keys, encrypted passwords, TSIG keys, tokens, license keys). The parser reads only an **allowlist of object types**, drops any **sensitive property** (by name pattern or encrypted `{0}_{aes}_` value) before it reaches the rest of the app, and never stores secrets ✅.
2. **NIOS CSV exports** (Global CSV Export or per-object) — mapped to the same data model as backups.
3. **WAPI**
   - Pull a fresh grid backup and parse it through the same pipeline.
   - Collect live operational data: member and service status, HA state, DHCP utilization and lease counts, NIOS version and hotfixes.
   - WAPI version auto-detected; paging for large result sets.
   - TLS: certificate fingerprint pinned per grid on first connect (SSH-host-key style); a client's CA bundle can also be loaded. Verification is never simply disabled.
4. **Location data** — manual entry, CSV upload, or mapped from grid extensible attributes (§10).
5. **Network tool results** — ping, SNMP, SSH command output, DNS lookups (§11).

---

## 5. Outputs

- **Reports** — Word/PDF and Excel, with the engineer's branding.
- **`pfr inspect <backup> [--json]`** — text summary or full JSON inventory ✅.
- **NIOS CSV import files**
  - Generated from findings or selected inventory.
  - One file per object type and action (add / modify / delete); modifications use the NIOS `_new_` field convention.
  - Validated against required fields and the target NIOS version before writing; previewed before saving.
  - Never applied by the app — the engineer imports them through Grid Manager (unless applied via WAPI RW mode, §6).
  - Round-trip safe, which also supports grid-to-grid migrations.
- **Location CSV templates** — prefilled with the client's current objects.
- **Audit log export** of WAPI writes, for change records.
- **Copy from tables** — multi-select rows and copy with headers ✅.

---

## 6. WAPI read-only / read-write mode

- **Read-only by default** on every connection.
- Read-write is enabled deliberately, **per grid connection, for the current session only**; it never persists.
- **Visible mode:** GUI shows a clear banner (with grid name) while a connection is RW. CLI requires `--rw` for any write; `--yes` skips confirmation only together with `--rw`.
- **Two layers of enforcement:** the app blocks all write calls in RO mode; separate grid accounts are recommended (read-only admin group for RO, scoped read-write group for RW) so grid permissions are the backstop.
- **Policy lock:** a setting (global or per client) that disables RW entirely.
- **Shared change sets:** any remediation change set can be saved as NIOS CSV **or** applied over WAPI, using the same preview and validation.
- **Write safeguards:** preview with before/after values; explicit confirmation showing grid name and change count; optional pre-change grid backup over WAPI; second confirmation for bulk deletes above a configurable threshold.
- **Partial failures:** stop and report exactly what was and wasn't applied; no automatic retry.
- **Audit log:** every write recorded per client — timestamp, grid, account, object, before/after values, result.
- [PENDING] Whether RW should be limited to particular kinds of changes.

---

## 7. Credential vault

- **Stored per grid:** grid master address, RO and RW usernames/passwords, pinned certificate fingerprint, associated client, allowed modes. Also SNMP communities / v3 credentials, SSH passwords/keys and pinned host keys.
- **Encryption:** AES-256-GCM; key derived from a **master password** using PBKDF2-SHA256 (high iteration count).
- **Optional OS-keychain unlock** on Windows (Credential Manager) and macOS (Keychain). On headless Linux CLI, master password is prompted or supplied via environment variable.
- [PENDING] Master password on every launch vs. OS-keychain unlock allowed (possibly per client).
- **Handling rules:** auto-lock after idle and on close; credentials never in logs, reports, audit entries, CSV output or errors; stored separately from client workspaces; deleting a workspace offers to delete its credentials; encrypted export/import only; a forgotten master password means resetting the vault.

---

## 8. Application shell (GUI) ✅

- Header: product and version, loaded grid (name · NIOS version · file), **Open backup…**.
- Module tabs: Clients, Inventory, Network tools, **Reports**. Opens on Clients.
- Header **Client › Grid switcher**; **save bar** for backups not yet saved to a client.
- Status bar with load results and errors.
- A backup can also be opened from the command line: `PFR <backup file>`.
- Backups are parsed off the UI thread; opening another backup replaces the current one.
- Errors while loading are shown in the status bar and never close the app; the previously loaded grid stays on screen.
- **Dark theme** with charcoal surfaces (not pure black).

### 8.1 Tables and trees ✅
- **Flat tables:** multi-word search across all columns; header sorting (numeric for IPs, prefixes, counts and TTLs); resizable columns; multi-select copy with headers.
- **Tree tables:** indentation with twisties; **Expand all / Collapse all**; Left/Right arrow keys and double-click to expand/collapse; search keeps matching rows plus their ancestors and opens those branches; header sorting reorders siblings without breaking the hierarchy.
- Group rows (views, domains, containers, members) are bold and show counts.
- Status text is colored: **red** for expired, **amber** for expiring.

---

## 9. Module 1 — Client workspaces ✅ (first version)

### 9.1 Structure
- **Client** → contacts, notes, engagement log, and one or more **grids**.
- **Grid** → identified by the **grid name** inside its backups; has a display name (renameable) and dated **snapshots**.
- **Snapshot** → the parsed inventory of one backup (**no secrets**), plus the source file's name, size and SHA-256 and an optional label. The original backup is not kept.
- An import goes to the client's grid with the same grid name (even if the grid was renamed), or creates a new grid. Importing the same file twice is allowed and noted.
- **A backup without a grid name is refused** — not loaded, not stored — in the GUI, CLI (`inspect`, `import`) and core, because the grid can't be identified.

### 9.2 Storage and security
- Data lives in a **`PfrData` folder next to the application**, so app and data travel together (e.g. on a USB drive). Overridable with `PFR_DATA_DIR` or the CLI's `--data`.
- **Encrypted with a master password:** PBKDF2-SHA256 (600,000 iterations, random salt) derives the key; each file is AES-256-GCM with a fresh nonce and its relative path bound in, so tampering or moving files between clients is detected. Folder names are random ids — client and grid names never appear unencrypted. Writes are atomic.
- The key is held only in memory: **Lock** (and closing the app) forgets it. Locking also clears any client grid shown in Inventory.
- A forgotten master password cannot be recovered (stated at setup). The same master password will protect the credential vault (§7).

### 9.3 Working with clients and grids (GUI)
- **Clients tab:** first-run setup (create master password), unlock, client list with each client's **grids beneath it**; clicking a grid opens its latest snapshot.
- **Client page:** *Details* (name, notes, contacts), *Grids* (import backup, save the grid open in Inventory, open latest, rename, delete; snapshots per grid with open/delete), *Engagement log* (dated entries with title and text, optionally linked to a grid snapshot).
- **Header switcher** lists every *Client › Grid*; choosing one opens its latest snapshot **instantly — no backup needed**.
- The **last grid viewed is reopened** automatically after unlock.
- A backup opened with **Open backup…** shows a **save bar** to store it under a client.

### 9.4 CLI
`pfr init`, `pfr clients`, `pfr client-add <name>`, `pfr import --client <name> <backup> [--label]`, `pfr grids --client <name>`, `pfr show --client <name> --grid <name> [--json]`. The master password is prompted without echo, or taken from `PFR_MASTER_PASSWORD`.

### 9.5 Later
- Compare snapshots ("what changed since last time").
- Findings tracked with status (open / accepted / remediated) — with Reports (§12).
- Network tool results saved per client.
- Change master password; idle auto-lock.

---

## 10. Module 2 — Inventory ✅ (locations pending)

### 10.1 Overview ✅
- Grid name, NIOS version, Grid Master, Master candidates, **next license expiry** (highlighted when expired or due).
- Summary cards: members (HA pairs), DNS views, zones (forward/reverse), DNS records (top types and host object count), networks (containers, network views), DHCP ranges (in failover).

### 10.2 Sections ✅
| Section | Layout | Contents |
|---|---|---|
| Members | Table | Host name, role (Grid Master / Candidate / Member), **services**, IPv4/IPv6, model, platform, serial(s), HA, nodes, upgrade group |
| Licenses | Tree: grid-wide → member → node licenses | Friendly name, code, node (HA nodes by position and serial), kind, expiry (or Permanent), status |
| DNS views | Table | View, network view, zones, records, recursion, DNSSEC validation, disabled |
| Zones | Tree: view → top-level domain (or IPv4/IPv6 reverse) → zone → sub-zone | Type, primary, **default TTL and source**, records, grid primaries/secondaries, other name servers, DNSSEC, disabled |
| Networks | Tree: network view → container → network (by containment) | Kind, IP version, DHCP ranges (rolled up to containers), comment, disabled |
| DHCP ranges | Tree: network view → network → ranges | Range, served by (member / failover), association, name, comment, disabled |
| DHCP failover | Table | Name, Infoblox/Microsoft, primary/secondary (member or server address), mode, ranges |
| DNS records | Tree: same as Zones, records under their zone (apex `@` first) | Name, type, value (zone-file style), **TTL and source**, **Host** flag, FQDN, disabled |

### 10.3 Data rules ✅
- **DNS views:** only real views; NIOS internal roots (`idns_root`, `non_DNS_host_root*`) are hidden.
- **Zones:** user zone types only (Authoritative, Forward, Delegated, Stub, Response Policy); each zone nests under its nearest ancestor zone in the same view.
- **Networks:** only containers hold other prefixes; networks are always leaves.
- **Member services:** DNS, DHCP, NTP, TFTP, HTTP file distribution, FTP, Discovery, Threat Protection, Threat Analytics, DFP, Captive Portal, TAXII, Cloud DNS Sync from per-member switches; **DTC** for DNS members with a DTC license (DTC has no switch of its own); **Reporting** for the reporting server (indexer), not report senders.
- **Licenses:** per-node licenses, grid-wide licenses and each member's next expiry (earliest of NIOS's summary and the member's own license dates); expiring within 90 days is flagged; license keys are never read.
- **DNS records:** A, AAAA, CNAME, PTR, MX, SRV, TXT, NAPTR, CAA. NIOS's auto-generated NS records are hidden; **delegation NS** records are listed under each delegated zone. DNSSEC (DNSKEY, RRSIG, NSEC3, DS) and DHCID are system-managed and not listed.
- **Host objects** expand into the records they serve — A/AAAA per address, PTR in the linked reverse zone when the grid hosts it, CNAME per alias — flagged in the **Host** column. Non-DNS (IPAM-only) hosts produce no records.
- **TTL inheritance:** grid default → zone override (SOA) → host object → record. Records without their own TTL inherit their zone's; host TTLs apply to every record the host generates; delegation NS records take the parent zone's default; delegated/forward/stub zones have no default TTL. Stale TTL values on inheriting records are ignored, as NIOS does. Shown as `28800 (8h)` with the source (Record / Host object / Inherited: zone / Inherited: grid).

### 10.4 Locations (pending)
- **Sites** defined once per client. Proposed fields: name, address, city, state/region, country, building, floor, room/rack, optional latitude/longitude, site contact, notes. [PENDING] confirm fields.
- **Members / nodes:** exactly one site, per node (HA nodes may be at different sites).
- **Networks, containers, DHCP ranges:** one or more sites, **or Global** (exclusive); inherited container → network → range unless overridden; optional suggestions from serving DHCP members.
- **DNS zones:** [PENDING].
- **Setting:** manual (single/bulk), CSV upload (app-generated template, `;`-separated sites, `Global` keyword), or grid extensible attributes. All go through preview, matching and conflict reporting. Precedence: manual > CSV > EA.
- Locations persist across imports; new objects without a location are flagged; optional write-back to grid EAs.
- Site filter includes Global objects by default; per-site reports count each object once in grid-wide totals.

---

## 11. Module 3 — Network tools

- **Ping:** single host, list, or all members of a grid/site — reachability, latency, loss.
- **SNMP:** GET/GETNEXT/WALK; v1, v2c, v3; Infoblox MIBs for readable names. [PENDING] versions in use.
- **SSH:** interactive console and scripted NIOS CLI command capture; host keys pinned. [PENDING] OS SSH client vs embedded terminal; [PENDING] targets beyond grid members.
- **DNS lookups:** dig-style queries (any type, chosen server, UDP/TCP, DNSSEC flags); compare mode across all DNS members; batch and reverse lookups.

---

## 12. Module 4 — Reports

Renamed from "Health check". Will hold **predefined reports** (health checks, CNAME resolution, licenses, …) and a **data explorer** for the stored grid data. [PENDING] design — see §14.

### 12.1 Engine
- Rules with severity levels, defined in YAML/JSON files loaded at runtime.
- Findings show affected sites and link to remediation change sets (NIOS CSV / WAPI).

### 12.2 Starter rules
- NIOS version drift; versions past end of support.
- DHCP exhaustion; ranges without failover; failover out of sync.
- Zones with no secondaries; lame/orphaned delegations; missing reverse zones.
- Open recursion; DNSSEC validation off; RPZ/DNS Firewall missing where licensed.
- Stale or duplicate A/PTR records.
- Backups not scheduled; NTP not configured.
- Default or overly broad admin permissions.
- **Licenses expired or expiring** (data available ✅).
- **Empty DNS views** (e.g. a view with no zones).

### 12.3 Location-aware rules
- Ranges served only by members at a different site; multi-site ranges without failover; objects with no location.

### 12.4 Live / network-tool rules
- Member unreachable by ping or SNMP; services down; SOA serials out of sync; DNS answers differing between servers; high CPU/memory/disk.

### 12.5 CNAME resolution report
- **Offline** from the backup (chains followed through grid-hosted zones per view; external targets marked "not verified") and **live** via a chosen resolver. [PENDING] default resolver.
- Failures: NXDOMAIN, NODATA, loops, chains over a maximum length (default 8), disabled targets, lame delegations (live); timeouts/SERVFAIL reported separately.
- Also: **subdomain-takeover risk** (dangling CNAME to cloud/SaaS hostnames; editable provider list), CNAME at apex or beside other data, MX/NS/SRV pointing at a CNAME, long working chains.
- Output: per CNAME with full chain, status, reason, severity and location; summary counts; NIOS CSV delete file (or WAPI RW); dated results for engagement comparison.

---

## 13. Out of scope (for now)

- **Forward Networks** integration (snapshot/NQE import, Infoblox ↔ network correlation). Deferred; architecture leaves room for a separate module.
- BloxOne / Universal DDI.
- Plain (non-NIOS-format) CSV output.

---

## 14. Open decisions

| # | Question | Section |
|---|---|---|
| 1 | Master password on every launch, or OS-keychain unlock allowed (per client?) | §7 |
| 2 | Limit WAPI read-write to specific change types, or open with safeguards | §6 |
| 3 | Confirm site fields | §10.4 |
| 4 | Multi-site / Global locations for DNS zones | §10.4 |
| 5 | SSH console: OS SSH client, embedded terminal, or OS client first | §11 |
| 6 | SSH targets: Infoblox members only, or other devices | §11 |
| 7 | SNMP versions in use (v3 only, or v2c too) | §11 |
| 8 | Default resolver for live CNAME checks | §12.5 |
| 9 | Report branding (logo, company name) | §5 |

| 10 | Reports: which predefined reports first; what "explore the database" should allow | §12 |

Resolved: sample backup provided (lab grid, NIOS 9.0.8); client workspace storage (next to the app), encryption (master password) and snapshots (parsed inventory only).
