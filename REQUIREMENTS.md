# PFR_MCP_V4 — Requirements

Status: **Draft v0.5** — updated 2026-10-28. Items marked **[PENDING]** are open decisions (see [Open decisions](#15-open-decisions)).
Items marked **✅** are implemented.

---

## 1. Purpose

A tool for an Infoblox engineer servicing multiple clients. It:

- inventories NIOS grids from backups, CSV exports or WAPI, with location context,
- builds a client-wide **IPAM** view of networks, DHCP ranges, hosts and address records across its grids and, later, external sources such as Forward Networks and NetBrain,
- tracks each client engagement over time,
- audits grids and reports health-check findings,
- provides network diagnostic tools (ping, SNMP, SSH, DNS),
- produces client-ready reports, and
- remediates issues through generated NIOS CSV import files or directly over WAPI (when read-write mode is enabled).

Scope is **NIOS (on-prem grids)**. External network sources (Forward Networks, NetBrain) come in through IPAM imports (§13.5); dedicated connectors for them are **deferred** (see §14).

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
| Extensibility | Modular so new modules can be added without reworking the core; new IPAM sources (e.g. Forward Networks, NetBrain) are added as adapters (§13.5) |

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
6. **External network sources** (later) — networks and devices from Forward Networks, NetBrain or a generic CSV/JSON file, imported into IPAM (§13.5).

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
- **IPAM import templates** — CSV/JSON templates for networks and devices (§13.5).
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

- **Grid connections ✅:** each grid can have a saved connection — grid master address, port, optional WAPI version, and a **read-only** and an optional **read-write** account (user id + password). Kept **with the client**, in the same encrypted store and under the same master password (§9.2), as a separate encrypted file per grid (`connection.pfr`) so passwords are never loaded when clients are listed. Edited in the GUI (Grids › Connection tab) and with `pfr connection-set / -show / -remove` (§9.4).
- **Client site logins and API keys ✅:** a client can also have any number of logins for **its own sites and services** (portals, VPNs, cloud consoles, ticketing...). Each has a unique title, site/URL (free text), user id, password, API key and notes; user id, password and API key are each optional, so this covers a plain login, an API-key-only service, or both. Stored encrypted with the client in its own file (`logins.pfr`). GUI: client page › *Logins & keys*; CLI: `pfr logins / login-set / login-show / login-remove` (§9.4).
- **Still to add to the connection:** pinned certificate fingerprint, allowed modes. Also SNMP communities / v3 credentials, SSH passwords/keys and pinned host keys.
- **Encryption:** AES-256-GCM; key derived from a **master password** using PBKDF2-SHA256 (high iteration count).
- **Optional OS-keychain unlock** on Windows (Credential Manager) and macOS (Keychain). On headless Linux CLI, master password is prompted or supplied via environment variable.
- [PENDING] Master password on every launch vs. OS-keychain unlock allowed (possibly per client).
- **Handling rules:** auto-lock after idle and on close; credentials never in logs, reports, audit entries, CSV output, errors or snapshots; **stored with the client** (changed from "separately from client workspaces"), so deleting a grid or client deletes its credentials with it ✅; passwords and API keys masked in the GUI ("Show" to reveal) and hidden by the CLI's `connection-show` / `login-show` unless `--reveal` ✅; never accepted as command-line arguments (prompted without echo, or taken from `PFR_RO_PASSWORD`, `PFR_RW_PASSWORD`, `PFR_LOGIN_PASSWORD`, `PFR_LOGIN_API_KEY`) ✅; encrypted export/import only; a forgotten master password means resetting the vault.

---

## 8. Application shell (GUI) ✅

- Header: product and version, loaded grid (name · NIOS version · file), **Open backup…**.
- Module tabs: Clients, Inventory, Network tools, **Reports**; an **IPAM** tab (after Inventory) is planned (§13). Opens on Clients.
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
- **Client** → contacts, notes, engagement log, **logins and API keys** for its own sites (§7), and one or more **grids**.
- **Grid** → identified by the **grid name** inside its backups; has a display name (renameable), dated **snapshots**, and an optional **connection** (§7).
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
- **Client page:** *Details* (name, notes, contacts), *Grids* (import backup, save the grid open in Inventory, open latest, rename, delete; per selected grid a *Snapshots* tab with open/delete and a *Connection* tab for its WAPI address and accounts), *Engagement log* (dated entries with title and text, optionally linked to a grid snapshot), *Logins & keys* (the client's own sites: title, URL, user id, password, API key, notes; secrets masked until shown).
- **Header switcher** lists every *Client › Grid*; choosing one opens its latest snapshot **instantly — no backup needed**.
- The **last grid viewed is reopened** automatically after unlock.
- A backup opened with **Open backup…** shows a **save bar** to store it under a client.

### 9.4 CLI
`pfr init`, `pfr clients`, `pfr client-add <name>`, `pfr import --client <name> <backup> [--label]`, `pfr grids --client <name>`, `pfr show --client <name> --grid <name> [--json]`. The master password is prompted without echo, or taken from `PFR_MASTER_PASSWORD`.

Grid connections: `pfr connection-set --client <name> --grid <name> [--address <host>] [--port <n>] [--wapi-version <v|auto>] [--ro-user <id>] [--rw-user <id>]` (options left out keep their saved values; passwords are prompted without echo, or taken from `PFR_RO_PASSWORD` / `PFR_RW_PASSWORD`), `pfr connection-show --client <name> --grid <name> [--reveal]`, `pfr connection-remove --client <name> --grid <name>`. `pfr grids` marks grids that have a saved connection.

Client site logins and API keys: `pfr logins --client <n>` (no secrets shown), `pfr login-set --client <n> --title <t> [--url <site>] [--user <id>] [--notes <text>] [--new-title <t>] [--password] [--api-key]` (creates or updates by title; `--password` / `--api-key` prompt without echo or read `PFR_LOGIN_PASSWORD` / `PFR_LOGIN_API_KEY`), `pfr login-show --client <n> --title <t> [--reveal]`, `pfr login-remove --client <n> --title <t>`.

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

## 12. Module 4 — Reports ✅ (first two reports)

Renamed from "Health check". Holds **predefined reports** and a **data explorer**. Reports run on the grid currently open (picked with the header's Client › Grid switcher or Open backup…), from the stored snapshot — no reloading of backups.

### 12.0 Available now ✅
- **License expiry** — every license (per member/node and grid-wide) with expiry date and days left, soonest first; expired in red, expiring within a chosen window (30/60/90/180/365 days, default 90) in amber; each member's next expiry from NIOS's license summary (subscription licenses) included when it adds information.
- **Dangling CNAMEs** — offline check described in §12.5; summary counts (dangling, takeover risks, not verified, resolving); filters to show resolving CNAMEs and targets outside the grid. Host aliases included; RPZ rules excluded.
- **Explore data** — placeholder only. Planned: list the tables (object types) found in the backup XML, show their fields, and query against them.

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
- **IPAM correlation rules** — overlapping prefixes, networks and addresses missing from a source, records outside any network (§13.7).

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

## 13. Module 5 — IPAM (planned)

A client-wide **address plan**: one tree of networks, with the DHCP ranges, hosts and address records inside them, built from every source the client has — first its NIOS grids (from their stored snapshots), later external network sources such as Forward Networks and NetBrain. IPAM belongs to the **client**, not to a grid: each network exists **once** in the plan and records which grids and external sources it is **present in**.

### 13.1 What it is, and how it differs from Inventory
- Inventory (§10) shows what **one grid** holds. IPAM shows the **client's address plan across all of its grids** and, later, across external sources.
- IPAM is a **read-only analysis view**. Nothing in it is written to a grid or to an external source; changes to grids still go through remediation (§5, §6).
- It works on a **client**: its grids' stored snapshots and its external imports. A backup that is open but not saved to a client has no IPAM view until it is saved.
- First version: NIOS grids only, built on the source-neutral model of §13.5 so external sources slot in without rework.

### 13.2 Keys and presence
- **The IP address is always the key.** Networks and containers are keyed by **prefix**, address-level objects by **IP address** (a DHCP range by its start–end addresses). Names, network views / VRFs and sources are attributes, never part of a key; a network view only groups the tree (§13.3).
- IPAM is **per client**. A **network object** (a container or network) exists **once** in the client's plan, keyed by its prefix, and carries a **Present in** attribute: the list of **grids** and **external sources** where it exists. 10.1.0.0/16 on three grids is one network present in three grids, not three networks.
- Each presence keeps what that source says about the network — its network view or VRF, kind (container / network), comment and disabled flag, and for a grid the snapshot it came from. The row shows a value when all presences agree and **varies** when they don't, with the per-source values visible in the row's details.
- Objects *inside* a network — DHCP ranges, hosts, A / AAAA / PTR records, devices and their interface addresses — are **not merged**: each is listed **once per grid or source it exists in**, with its own single **Grid / Source**, even when the same host, device or address is on several.
- Presence is derived, not typed in. NIOS grids contribute from their **latest snapshot** (no backup needed; the snapshot date is shown per grid, "as of"); external sources contribute from their newest import batch (§13.5). Adding a snapshot or batch, or deleting a grid or source, updates it.
- Matching is by prefix alone. Identical prefixes in different network views or VRFs (for example unrelated private space) are therefore one network, present in both; the tree shows it under each of those views, and whether that is intended or a mistake is a finding (§13.7).

### 13.3 The tree
Looks and behaves like the Networks tree (§10.2) — **network view → container → network**, nested by containment — with the table and tree rules of §8.1 (multi-word search that keeps matches and their ancestors, header sorting among siblings, Expand all / Collapse all, multi-select copy with headers, bold group rows with counts). The network view groups the tree but is not part of any key (§13.2). IPAM adds a **Present in** column (the grids and external sources) and puts the address-level objects below each network.

| Level | Rows | Notes |
|---|---|---|
| 1 | Network view | NIOS network view; an external source's VRF / routing-instance names group into views by name [PENDING] |
| 2 | Containers | Nested by containment |
| 3 | Networks | Leaves of the address plan |
| 4 | Under each network: DHCP ranges, hosts, A / AAAA records, PTR records, device interface addresses | Created when the network is expanded; counts are rolled up to every level above |

Columns: address / prefix · kind · **Present in** (the grids and external sources of a network within that view; the single grid or source of an object inside one) · IP version · rolled-up counts (DHCP ranges, hosts, records, devices) · comment · disabled.

Placement rules:
- An address-level object sits under the **most specific network that contains its address**, in the same network view. It is placed whichever grid or source the network is present in, so a record on one grid can sit under a network that only another grid has. An address in no network goes under a **(no network)** node for that view, so records outside any managed network are visible.
- DNS records take the network view of their DNS view (§10.3) and are placed by their address: A / AAAA by the address in the record, PTR by the address decoded from the reverse name. Hosts, ranges and device interface addresses use their own network view / VRF and addresses.
- Records generated by a **host object** are shown **under the host**, not repeated as separate rows (as with the Host flag in §10.3).
- A prefix present in several grids or sources is **one node** whose **Present in** lists them all. A prefix present in more than one network view is still **one network object** (the key is the prefix): it appears under each of those views with that view's presences, and is counted once in totals and reports. Partially overlapping (different) prefixes are flagged. None of this is an error by itself.
- Nesting is by containment within a network view, across all grids and sources, so a network from one source can sit inside a container from another. A node is shown as a **container** if any of its presences is a container.
- **Filters:** grid or source (matches any presence), present in more than one grid, in a grid but in no external source, in an external source but in no grid, network view / VRF, object kind (networks only, plus ranges, hosts, records, devices) and IP version. Very large grids (hundreds of thousands of records) stay responsive because leaf objects are built on expansion.

### 13.4 Objects
| Object | Key | Source | Fields |
|---|---|---|---|
| Network view | — (grouping only) | NIOS; external VRF | Name, present in, networks |
| Container, network | Prefix | NIOS; external | IP version, kind (container / network), comment or description, disabled, site (§10.4, once locations exist), and **present in**: the grids and external sources it exists in, each presence keeping its own network view or VRF, kind, comment and disabled flag |
| DHCP range | Start–end addresses | NIOS | Served by (member / failover), association, name, comment, disabled, grid |
| Host | Each of its IP addresses | NIOS host objects | Name, IPv4 / IPv6 address, DNS / DHCP flags, aliases, TTL and source, disabled, grid. **IPAM-only (non-DNS) hosts are included**: they occupy addresses even though Inventory lists no records for them |
| A / AAAA record | The address in the record | NIOS | Name, zone and DNS view, TTL and source, disabled, grid |
| PTR record | The address decoded from the reverse name | NIOS | Reverse name, target name, zone and DNS view, TTL and source, disabled, grid |
| Device | Management address | External sources | Name, type, vendor, model, OS / version, site, source |
| Device interface address | The interface address | External sources | Device, interface, prefix length, VRF, status, source |

- Only records that carry an address are part of IPAM. CNAME, MX, SRV, TXT, NAPTR, CAA, NS and system-managed records stay in Inventory (§10.3). The TTL rules of §10.3 apply to the A / AAAA / PTR rows.
- [PENDING] Whether fixed addresses, reservations and (over WAPI) leases are included.
- **Only networks are merged across sources** (through **Present in**). Devices, hosts, DHCP ranges, A / AAAA / PTR records and device interface addresses are **listed once per grid or source they exist in**: a device known to both Forward Networks and NetBrain appears twice, and a host with the same name on two grids appears twice, each row with its own Grid / Source.
- Names (host, device, record) are never keys. A host with several addresses is listed under each address, same-named objects at different addresses are separate rows, and one address on two grids is two rows.

### 13.5 External sources and import
- IPAM is **source-neutral**: it understands an **import model**, not Forward Networks or NetBrain. Each external source is an **adapter** that turns its export (or, later, its API response) into that model. Adding a source means adding an adapter, not changing IPAM.
- **Import model.** Every row needs its IP key; a row without one is rejected with its line and reason. *Networks:* prefix (required, the key), name or description, VRF or network view, VLAN, site, tags, source reference. *Devices:* management address (required, the key), name, type, vendor, model, OS / version, site, and their interfaces (address / prefix, required and the key of that interface; name, VRF, status, description). [PENDING] confirm fields.
- **Ways to import**, in order of delivery:
  1. **Generic CSV / JSON** with a documented schema and app-generated templates (like the location templates, §5). Works for any source and fully offline, so Forward Networks and NetBrain data can be loaded as soon as the engineer can export it.
  2. **Forward Networks adapter** — reads its exported snapshot / NQE query results into the model.
  3. **NetBrain adapter** — reads its exported network and device data into the model.
  4. **Direct pull over the source's API** (optional) — user-initiated only (§3), using the client's saved logins and API keys (§7).
- [PENDING] Which exports and formats each product provides in the client's environment, and whether direct API pull is wanted.
- **Process:** choose the client and source, pick a file, then **parse, validate and preview** — counts of networks and devices, what is new, changed or gone since that source's previous batch, and any invalid rows — and only then store it. Bad rows are listed with their line and reason; a failed import stores nothing.
- **Batches:** each import is a dated **batch** with a label and the file's name, size and SHA-256, like snapshots (§9.1), holding no secrets. The newest batch per source is what IPAM shows; older ones are kept for comparison (§9.5). Importing the same file twice is allowed and noted.
- **Matching:** an imported network that already exists in the plan (same prefix, whatever its network view or VRF) is not duplicated: the source is added to that network's **Present in**. Imported addresses (interfaces, hosts, records) are placed under the network that contains them, like any other. Devices and the other objects are never matched or merged.

### 13.6 Storage and refresh
- NIOS data is not copied: IPAM reads each grid's stored snapshots (§9.1). External batches are stored **encrypted** with the client under the master password, like snapshots (§9.2) — for example `clients/<client>/ipam/<source>/<batch>.pfr`.
- IPAM is built when opened and rebuilt when a snapshot or batch is added or deleted. Deleting a grid removes it from every network's **Present in** (a network present only there disappears, along with the objects that were on that grid), deleting a source does the same for its batches, and deleting a client deletes everything.

### 13.7 Correlation and reports (later)
- Candidate findings, feeding the rules engine (§12.1): **presence gaps** — a network present in some of the client's grids but not others, in an external source but in no grid, or in a grid but in no external source; the same prefix in different network views or VRFs; partially overlapping prefixes; address records outside any managed network, or on a grid that doesn't have the network; the same address held by hosts on different grids; device interface addresses with no A or PTR record; A records without a matching PTR; networks with no DHCP range; objects with no location.
- This is the Infoblox ↔ network correlation that was previously deferred together with Forward Networks.

### 13.8 GUI and CLI
- **IPAM** module tab, after Inventory, with sections *Address plan* (the tree), *Devices* (table) and *Sources* (the client's grids with their snapshot dates, the external sources with their batches, and **Import…**).
- CLI (planned): `pfr ipam --client <name> [--grid <name>] [--source <name>] [--json]`, `pfr ipam-sources --client <name>`, `pfr ipam-import --client <name> --source <name> <file> [--label <text>] [--dry-run]`.

---

## 14. Out of scope (for now)

- **Forward Networks and NetBrain connectors** — dedicated adapters and direct API pull. Deferred; until then their networks and devices can be loaded through the generic IPAM import (§13.5), which is built so the adapters can be added later.
- BloxOne / Universal DDI.
- Plain (non-NIOS-format) CSV output.

---

## 15. Open decisions

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
| 10 | Explore data: query language/UI (and whether it reads the raw XML or the stored snapshot) | §12 |
| 11 | IPAM: how the network views of different grids and the VRFs / routing instances of external sources are grouped into the tree's views (default: by name, editable) | §13.3 |
| 12 | IPAM: include fixed addresses, reservations and leases | §13.4 |
| 13 | IPAM import model: confirm the network and device fields | §13.5 |
| 14 | Forward Networks and NetBrain: which exports/formats are available, and whether direct API pull is wanted | §13.5 |
| 15 | IPAM: a device with no management address — reject it, or key it by its first interface address | §13.5 |

Resolved: first reports are license expiry and dangling CNAMEs; sample backup provided (lab grid, NIOS 9.0.8); client workspace storage (next to the app), encryption (master password) and snapshots (parsed inventory only).
