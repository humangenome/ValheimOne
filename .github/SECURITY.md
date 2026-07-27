# Security Policy

## Reporting a vulnerability

If you've found a security issue in ValheimOne (the live map web server, the admin console, the share-token view tiers, the A2S query responder, or the Discord integration), please **do not** open a public GitHub issue.

Email security reports to: **security@humangenome.dev**, or open a private security advisory on GitHub at https://github.com/HumanGenome/ValheimOne/security/advisories/new — that's the preferred channel.

Include:
- A description of the vulnerability
- Steps to reproduce
- Affected component (live map / console / share tokens / query / Discord / a gameplay module)
- ValheimOne version (`vo doctor`, or the plugin banner in the BepInEx log) and Valheim dedicated-server build
- Whether the issue is currently being exploited

We aim to acknowledge reports within 72 hours and provide a triage update within 7 days.

## Scope

In scope:
- Any path that lets a lower view tier read data belonging to a higher one — public or shared reaching admin-tier map data, player platform IDs, pins, or the crossplay join code
- A tokenless or empty-token request being served anything beyond the intended public surface
- Authentication bypass on the admin console or any authenticated `/api/*` route
- Command injection through the console command box, or execution of a command outside the allowlist
- Path traversal or arbitrary file read through any HTTP route
- A connected game client escalating privileges on the host through a module's synced overlay
- Secrets (tokens, passwords, webhook URLs) appearing in an API payload, the activity log, or the live map

Out of scope:
- Hardware-host vulnerabilities (those belong to your hosting provider)
- Vulnerabilities in Valheim itself (report to Iron Gate) or in BepInEx/Harmony
- Vulnerabilities in other BepInEx plugins running alongside ValheimOne
- Anti-cheat / cheating concerns — ValheimOne does not provide anti-cheat
- Admin console commands doing what an admin asked for. Protect the admin token instead.
- Deliberately publishing a share link, or setting `PublicPins = true` and then finding pins on the public view
- Exposing the map port to the internet without a reverse proxy. ValheimOne does not terminate TLS.
