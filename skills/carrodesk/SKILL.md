---
name: carrodesk
description: Control this Windows machine through CarroDesk.Cli.exe — query host/module status, keep the machine awake (indefinitely or for N minutes), schedule sleep or shutdown (default 30 s delay, cancellable), and start/stop allowlisted Windows services (GameViewerService/UUYC, TermService/RDP) honoring each service's PIN policy. Use this skill whenever the user mentions CarroDesk, UUYC, GameViewer, RDP/remote desktop service, asks to start/stop/restart any Windows service, asks whether the machine is awake, wants the machine status or module status, or asks to put the computer to sleep or shut it down — even if they never say "CarroDesk".
---

# CarroDesk Machine Control

CarroDesk is a tray-resident control service for this Windows machine. You drive it
through its CLI client (single-file exe), which forwards commands to the running
host process and returns JSON results.

- Client path: `C:\Home\Tools\CarroDesk\CarroDesk.Cli.exe` (referred to as `CarroDesk.Cli` below)
- **Prerequisite**: the host app CarroDesk.exe must be running. If the pipe is unreachable
  you get `error -32020: pipe transport failed` — tell the user to start the CarroDesk tray
  app; do not retry in a loop
- Each command is an independent short-lived process; concurrent calls are safe

## Usage template

```text
CarroDesk.Cli ctl <capability> [--json] [--pin <pin>] [--<param> <value>]
```

`--json` gives indented JSON. Exit codes: 0 success / 1 usage / 2 business error / 3 host unreachable.

## Keep the machine awake

```text
CarroDesk.Cli ctl awake.status     :: mode (passive/indefinite/timed/untiltime), isActive, remainingMinutes, pendingAction
CarroDesk.Cli ctl awake.on         :: keep awake indefinitely
CarroDesk.Cli ctl awake.on --minutes 120   :: keep awake for 2 hours (1-1440 allowed)
CarroDesk.Cli ctl awake.off        :: cancel (returns to passive; also suppresses process-link auto-re-enable)
```

Notes: awake state is runtime-only — it resets to the configured mode after a host restart.
`awake.on` / `awake.off` never require a PIN.

## Sleep / shutdown (delayed power actions)

```text
CarroDesk.Cli ctl awake.sleep              :: sleep (S3) after default 30 s delay
CarroDesk.Cli ctl awake.sleep --seconds 1800        :: sleep in 30 minutes (0-86400)
CarroDesk.Cli ctl awake.sleep.cancel       :: cancel a scheduled sleep
CarroDesk.Cli ctl awake.shutdown           :: shut down after default 30 s delay
CarroDesk.Cli ctl awake.shutdown --seconds 300      :: shut down in 5 minutes (0-86400)
CarroDesk.Cli ctl awake.shutdown.cancel    :: cancel a scheduled shutdown
```

Rules:
- **Reply to the user first**, then let the delay run — the default 30 s exists so your
  answer gets through before the machine goes down
- Sleep and shutdown share one pending slot: a new schedule replaces the old one;
  `awake.status` shows `pendingAction` / `pendingFireAt` / `pendingSecondsRemaining`
- The host clears its keep-awake state automatically before the scheduled sleep;
  `awake.off` does not touch a scheduled power action
- `awake.shutdown` is a privileged-risk action — confirm with the user before calling it

## Service control (start/stop Windows services)

Always check the allowlist first — it returns each service's `name`, `desc`
(human-readable), `requiresPin` (per-service PIN policy) and live `status`:

```text
CarroDesk.Cli ctl services.status
```

Start/stop (**whether a PIN is required is decided per service by `requiresPin`, not all
services need one**):

```text
:: GameViewerService (UUYC remote control) is configured PIN-free — call directly
CarroDesk.Cli ctl services.start --name GameViewerService

:: TermService (RDP) requires a PIN — ask the user for their 6-digit PIN first
CarroDesk.Cli ctl services.stop --name TermService --pin <pin provided by user>
```

PIN rules (violating them causes a lockout):
- For `requiresPin: true` services: **ask the user for the PIN**, pass it via `--pin`; never guess
- At most one retry on a wrong PIN; 5 wrong attempts trigger a lockout (`-32002 blocked`)
  during which even the correct PIN is rejected
- If you call a PIN-required service without a PIN and get `-32002`, go back and **ask the
  user** — do not retry blindly

## Status queries

```text
CarroDesk.Cli ctl host.status                :: version, uptime, module states
CarroDesk.Cli ctl host.status --json         :: indented JSON
CarroDesk.Cli ctl host.capabilities.list     :: full capability schemas (authoritative list)
```

In `host.status` / `host.modules.list`, the first entry `id:"host"` is the host pseudo-module
(normal); a real module with `capabilityCount: 0` simply has no capabilities yet (staged
rollout) — not a fault.

## Error code quick reference

| Code | Meaning | What you should do |
|---|---|---|
| -32601 | unknown capability | verify the name via capabilities.list |
| -32602 | invalid params (including service not in allowlist) | fix the params; if a service was rejected, tell the user to configure the allowlist |
| -32002 | PIN missing/wrong/lockout | ask the user; on lockout tell them to wait |
| -32003 | rate limited | wait and retry later |
| -32004 | execution timeout | check actual state later via services.status / awake.status |
| -32020 | host not running | tell the user to start CarroDesk |

## Boundaries (these do not exist — do not attempt)

- No arbitrary shell / PowerShell execution; system-level administration goes through
  the user's SSH/remote-control channels
- Only allowlisted services are operable; the kernel-level service DACL blocks everything
  else regardless of configuration
- To add a service or change its PIN policy, the user edits
  `C:\Home\Tools\CarroDesk\app_data\config.json` → `Services.AllowedServices`
  (`{name, desc, requiresPin}`) and grants service rights once via
  `docs/remote-admin/grant-service-control.ps1` (admin terminal)

## Deep reference

When unsure about usage, conventions or boundaries, read the embedded manual:

```text
CarroDesk.Cli ctl host.guide
```

Returns the full Markdown manual (auto-updates with the host).
