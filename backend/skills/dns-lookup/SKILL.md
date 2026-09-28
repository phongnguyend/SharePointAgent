---
name: dns-lookup
description: Query DNS records (A, AAAA, MX, TXT, NS, CNAME) on Windows or Linux and return structured JSON.
license: MIT
compatibility: Windows PowerShell 5.1 with Resolve-DnsName, or PowerShell 7 on Linux with dig (dnsutils or bind-utils)
metadata:
  author: phongnguyen
  version: "3.0"
allowed-tools: powershell
script_path: scripts/resolve-dns.ps1
---

# DNS lookup

Use this skill to resolve domain names or inspect DNS records for troubleshooting.

Run `scripts/resolve-dns.ps1` with an argument array, for example:
`["-Domain", "example.com", "-RecordType", "MX"]`.

- `Domain` (required): DNS name, not a URL. Labels allow letters, numbers, underscores and hyphens. An optional trailing dot is accepted.
- `RecordType`: A (default), AAAA, MX, TXT, NS or CNAME.
- `DnsServer`: Optional IPv4 or IPv6 resolver address. Otherwise use the execution host's configured resolver.

Windows uses `Resolve-DnsName`; Linux uses `dig`. Both return a JSON array with `Name`, `Type`, `TTL` and record-specific fields:

- A/AAAA: `IPAddress`
- NS/CNAME: `NameHost`
- MX: `Exchange`, `Preference` (including zero)
- TXT: `Text` (concatenated character-strings)

CNAME answers may accompany address records. Linux returns `[]` for a successful response without matching answers. DNS errors and invalid inputs exit with a nonzero status; never interpret an error as a successful empty result. The runner limits execution to 30 seconds.

Both application Docker images include PowerShell and dig. For Linux installations outside Docker, install PowerShell 7 and `dnsutils` (Debian/Ubuntu) or `bind-utils` (RHEL/Fedora). DNS results reflect the execution host's network and resolver.
