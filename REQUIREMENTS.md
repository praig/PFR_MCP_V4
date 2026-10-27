# PFR_MCP_V4 — Requirements

Status: **Draft v0.1** — captured 2026-10-27. Items marked **[PENDING]** are open decisions (see [Open decisions](#13-open-decisions)).

---

## 1. Purpose

A tool for an Infoblox engineer servicing multiple clients. It:

- audits NIOS grids and reports health-check findings,
- inventories grids with location context,
- tracks each client engagement over time,
- provides network diagnostic tools (ping, SNMP, SSH, DNS),
- produces client-ready reports, and
- remediates issues through generated NIOS CSV import files or directly over WAPI (when read-write mode is enabled).

Scope is **NIOS (on-prem grids)**. Forward Networks integration is **deferred** (see §12).

---

## 2. Platform and technology

| Item | Decision |
|---|---|
| Language / runtime | C# on .NET 8 or later |
| Desktop GUI | Avalonia (MIT-licensed core only) — Windows and macOS |
| CLI | Linux; also usable on Windows and macOS |
| Architecture | One shared core library used by both GUI and CLI (parsers, storage, rules, locations, credential vault, network tools, reporting, CSV generation, WAPI client) |
| Third-party components | Free/permissive licenses only (MIT, Apache 2.0, BSD). No paid Avalonia tiers or commercial libraries |
| Cryptography | .NET built-in cryptography only |
| License record | `THIRD-PARTY-NOTICES.md` ships with every build for client security reviews |
| Extensibility | Modular design so new modules (e.g. Forward Networks) can be added without reworking the core |

### 2.1 Why C#
- Self-contained single-file executables, cross-compiled for Windows, macOS and Linux from one machine.
- Nothing to install on target machines — suits air-gapped client environments (Python rejected: library dependencies are hard to satisfy air-gapped; packaged Python binaries are large and often flagged by AV/EDR).
- Avalonia provides a cross-platform desktop GUI under MIT.

### 2.2 Planned libraries (all free/permissive)
| Need | Library | License |
|---|---|---|
| GUI | Avalonia (core), Avalonia.Controls.DataGrid | MIT |
| Charts | ScottPlot or LiveCharts2 | MIT |
| SNMP | SharpSnmpLib | MIT |
| SSH | SSH.NET | MIT |
| DNS | DnsClient.NET | Apache 2.0 |
| Ping | System.Net.NetworkInformation (built in) | — |

---

## 3. Air-gapped and offline operation

- Runs on air-gapped machines with nothing to install.
- Distributed as **self-contained, single-file executables** per OS.
- No license keys, activation, telemetry, or update checks.
- The **only** network traffic is user-initiated, to targets the user specifies:
  WAPI, ping, SNMP, SSH, and DNS queries.
- All client data and credentials stay on the local machine.
- Health-check rules can be added or edited with a text editor — no recompiling.

---

## 4. Inputs

1. **Grid backup** (`.bak` / `.tar.gz` containing `onedb.xml`) — **primary source**.
   - Grid backups contain secrets (private keys, encrypted passwords, TSIG keys, tokens, license strings).
     The parser reads only an **allowlist of object types**, drops any **sensitive property** (by name
     pattern or encrypted `{0}_{aes}_` value) before it reaches the rest of the app, and streams the
     backup in place without extracting it to disk. Secrets are never stored in the client database.
2. **NIOS CSV exports** (Global CSV Export or per-object) — fully supported, mapped to the same data model as backups.
3. **WAPI**
   - Pull a fresh grid backup and parse it through the same pipeline.
   - Collect live operational data: member and service status, HA state, DHCP utilization and lease counts, NIOS version and hotfixes.
   - WAPI version auto-detected; paging for large result sets.
   - TLS: certificate fingerprint pinned per grid on first connect (SSH-host-key style); a client's CA bundle can also be loaded. Verification is never simply disabled.
4. **Location data** — manual entry, CSV upload, or mapped from grid extensible attributes (§9).
5. **Network tool results** — ping, SNMP, SSH command output, DNS lookups (§10).

---

## 5. Outputs

- **Reports** — Word/PDF and Excel, with the engineer's branding.
- **NIOS CSV import files**
  - Generated from findings or selected inventory.
  - One file per object type and action (add / modify / delete); modifications use the NIOS `_new_` field convention.
  - Validated against required fields and the target NIOS version before writing.
  - Previewed before saving.
  - Never applied by the app — the engineer imports them through Grid Manager (unless applied via WAPI RW mode, §6).
  - Round-trip safe (NIOS CSV export → app → NIOS CSV import format), which also supports grid-to-grid migrations.
- **Location CSV templates** — prefilled with the client's current objects.
- **Audit log export** of WAPI writes, for change records.

---

## 6. WAPI read-only / read-write mode

- **Read-only by default** on every connection.
- Read-write is enabled deliberately, **per grid connection, for the current session only**; it never persists.
- **Visible mode:** GUI shows a clear banner (with grid name) while a connection is RW. CLI requires `--rw` for any write; `--yes` skips confirmation only together with `--rw`.
- **Two layers of enforcement:**
  - The app blocks all write calls in RO mode.
  - Separate grid accounts are recommended: a read-only admin group for RO, a scoped read-write group for RW. Grid permissions are the backstop.
- **Policy lock:** a setting (global or per client) that disables RW entirely.
- **Shared change sets:** any remediation change set can be saved as NIOS CSV **or** applied over WAPI, using the same preview and validation.
- **Write safeguards:**
  - Preview of every object to be added / modified / deleted, with before and after values.
  - Explicit confirmation showing grid name and change count.
  - Optional pre-change grid backup pulled over WAPI.
  - Second confirmation (naming the count) for bulk deletes above a configurable threshold.
- **Partial failures:** stop, report exactly what was and wasn't applied; no automatic retry.
- **Audit log:** every write recorded per client — timestamp, grid, account, object, before/after values, result.
- [PENDING] Whether RW should be limited to particular kinds of changes.

---

## 7. Credential vault

- **Stored per grid:** grid master address, RO and RW usernames/passwords, pinned certificate fingerprint, associated client, allowed modes.
- **Also stores:** SNMP communities and v3 credentials, SSH passwords/keys, pinned SSH host keys.
- **Encryption:** AES-256-GCM; key derived from a **master password** using PBKDF2-SHA256 (high iteration count).
- **Optional OS-keychain unlock** on Windows (Credential Manager) and macOS (Keychain). On headless Linux CLI, master password is prompted or supplied via environment variable.
- [PENDING] Master password on every launch vs. OS-keychain unlock allowed (possibly a per-client setting).
- **Handling rules:**
  - Auto-locks after an idle period and when the app closes.
  - Credentials never appear in logs, reports, audit entries, CSV output, or error messages.
  - Stored separately from client workspaces — sharing or archiving a workspace never includes credentials.
  - Deleting a client workspace offers to delete its stored credentials.
  - Encrypted export/import for moving to a new machine; no plain-text export.
  - A forgotten master password means resetting the vault (stated clearly at setup).

---

## 8. Module 1 — Client workspace and tracking

- One workspace per client.
- Contacts, notes, engagement log.
- Multiple imports per client over time, kept as dated snapshots.
- Findings tracked with status: open / accepted / remediated.
- Comparison between engagements ("what changed since last time").
- Network tool results saved per client with timestamps.

---

## 9. Module 2 — Inventory and locations

### 9.1 Inventory
- Grid members and nodes (role, model, NIOS version, HA), licenses, DNS views and zones, record counts, networks and containers, DHCP ranges, failover associations.
- Live status from WAPI and SNMP when available.
- Searchable, filterable, groupable by site.

### 9.2 Sites
- Defined once per client, then assigned.
- Proposed fields: name, address, city, state/region, country, building, floor, room/rack, optional latitude/longitude, site contact, notes. [PENDING] confirm fields.

### 9.3 Assignment rules
| Object | Locations |
|---|---|
| Grid members / nodes | Exactly one site, **per node** (HA nodes may be at different sites) |
| Networks, network containers, DHCP ranges | One or more sites, **or Global** |
| DNS zones | [PENDING] |

- **Global** = not site-bound / applies everywhere. Exclusive with specific sites.
- **Inheritance:** container → network → range, unless overridden. Overrides are visible.
- **Suggestions:** the app may propose range locations based on the serving DHCP members' sites; suggestions only, never auto-assigned.

### 9.4 Setting locations
1. **Manual** — single or bulk; pick sites or Global.
2. **CSV upload** — app-generated template prefilled with current objects; multiple sites separated by `;`; `Global` keyword.
   Matching: members/nodes by hostname (fallback IP or serial); networks by network view + CIDR; ranges by start/end address.
3. **Grid extensible attributes** — configurable EA names (incl. multi-value EAs) mapped to sites.

All sources go through preview, matching, and conflict reporting.

- **Precedence:** manual > CSV > grid EA; each location shows its source.
- **Persistence:** locations persist across imports; new objects without a location are flagged.
- **Optional write-back** of locations to grid EAs via NIOS CSV or WAPI RW.

### 9.5 Filtering and reporting
- Site filter includes Global objects by default (toggle).
- Per-site report sections list multi-site objects under each of their sites; grid-wide totals count each object once.
- Global objects get their own section.

---

## 10. Module 3 — Network tools

### 10.1 Ping
- Single host, host list, or all members of a grid/site — reachability, latency, packet loss.
- .NET built-in ping; on Linux falls back to system `ping` without raw-socket privileges.

### 10.2 SNMP
- GET, GETNEXT, WALK; v1, v2c and v3 (auth/priv).
- Ships with Infoblox MIB definitions for readable names (service status, DNS/DHCP counters, CPU, memory, disk).
- [PENDING] Which SNMP versions clients typically use.

### 10.3 SSH
- **Interactive console** to members' NIOS CLI or other devices. [PENDING] Launch OS SSH client vs. embedded terminal tab.
- **Scripted command capture** — run NIOS CLI commands (e.g. `show status`, `show version`, `show ntp`) across members, save output to the client workspace; usable by health checks.
- Host keys pinned per device.
- [PENDING] SSH targets: Infoblox members only, or other network devices too.

### 10.4 DNS lookups
- dig-style queries: any record type, chosen server, UDP/TCP, DNSSEC flags.
- **Compare mode:** same query to every DNS member; highlight differing answers or SOA serials.
- Batch lookups from a list; reverse lookups.

---

## 11. Module 4 — Health check / audit

### 11.1 Engine
- Rules engine with severity levels; rules defined in YAML/JSON files loaded at runtime.
- Findings show affected sites and link to remediation change sets (NIOS CSV / WAPI).

### 11.2 Starter rules
- NIOS version drift across members; versions past end of support.
- DHCP range exhaustion; ranges without failover; failover out of sync.
- Zones with no secondaries; lame/orphaned delegations; missing reverse zones.
- Recursion open to any; DNSSEC validation off; RPZ / DNS Firewall missing where licensed.
- Stale or duplicate A/PTR records.
- Backups not scheduled; NTP not configured.
- Default or overly broad admin permissions.

### 11.3 Location-aware rules
- Ranges served only by members at a different site.
- Multi-site ranges without DHCP failover.
- Objects with no location assigned.

### 11.4 Live / network-tool rules
- Member unreachable by ping or SNMP; services down.
- SOA serials out of sync across members; DNS answers differing between servers.
- High CPU, memory, or disk (SNMP).

### 11.5 CNAME resolution report
**Resolution checking**
- *Offline (from backup):* follow each CNAME chain through all grid-hosted zones, per DNS view. Targets outside grid-hosted zones marked "external, not verified".
- *Live:* resolve external targets via a chosen resolver. [PENDING] Default resolver: a grid member vs. a resolver specified each time.
- Evaluated per DNS view (catches resolves-internally-but-not-externally).

**Failure conditions**
- Target NXDOMAIN.
- Target exists but has no A/AAAA (NODATA).
- Chain loop.
- Chain exceeds maximum length (default 8, configurable).
- Target is a disabled record or in a disabled zone.
- Lame/unreachable delegation for the target's zone (live).
- Timeout / SERVFAIL — reported separately from definite failures.

**Related issues in the same report**
- **Subdomain-takeover risk** (high severity): dangling CNAME pointing at a cloud/SaaS hostname (Azure, AWS, Heroku, GitHub Pages, …). Provider list kept in an editable rule file.
- CNAME at zone apex, or coexisting with other record types at the same name.
- MX, NS or SRV records pointing at a CNAME.
- Long but working chains (warning).

**Output**
- Per CNAME: view, zone, name, full chain, final status, failure reason, severity, site/location.
- Summary counts by status, zone, view.
- NIOS CSV delete file for confirmed dangling CNAMEs (or apply via WAPI RW with usual safeguards).
- Dated results; engagement comparison shows new vs. fixed dangling CNAMEs.

---

## 12. Out of scope (for now)

- **Forward Networks** integration (snapshot/NQE import, Infoblox ↔ network correlation). Deferred; architecture leaves room to add it as a separate module.
- BloxOne / Universal DDI.
- Plain (non-NIOS-format) CSV output.

---

## 13. Open decisions

| # | Question | Section |
|---|---|---|
| 1 | Master password on every launch, or OS-keychain unlock allowed (per client?) | §7 |
| 2 | Limit WAPI read-write to specific change types, or open with safeguards | §6 |
| 3 | Confirm site fields | §9.2 |
| 4 | Multi-site / Global locations for DNS zones | §9.3 |
| 5 | SSH console: OS SSH client, embedded terminal, or OS client first | §10.3 |
| 6 | SSH targets: Infoblox members only, or other devices | §10.3 |
| 7 | SNMP versions in use (v3 only, or v2c too) | §10.2 |
| 8 | Default resolver for live CNAME checks | §11.5 |
| 9 | Sanitized sample backups / CSV exports for parser development | §4 |
| 10 | Report branding (logo, company name) | §5 |
