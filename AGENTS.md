
# AGENTS.md

Operational guide for running, extending, and automating **Scheduled Command Executor** with an LLM from the command line.

> Latest release: **v2.8** — correct TZ/DST scheduling across machine vs. job TZ (Cronos with UTC base + job TZ), non-blocking scheduler loop, and `validateCron` lowercase alias.

> Target stack: **.NET 9.0**, Windows (service or console), `Cronos` for cron parsing, `HttpListener` for the dashboard/API.

---

## 1) Agent roles (who does what)

Define these “personas” in your Codex setup; pick one per task.

### 🧠 Core Engineer (Scheduler Agent)

* Owns `CommandExecutorService.cs`, `ConcurrencyManager.cs`, `SchedulerOptions.cs`.
* Guarantees: no crashes on bad config; correct TZ/DST math; graceful shutdown; concurrency safety.
* Key rules: never block the loop; log once per invalid cron; disabled jobs do not execute.

### 🖥️ Frontend Engineer (Dashboard Agent)

* Owns `dashboard.html` and Monitoring rendering.
* Guarantees: loads over `http://localhost:5058/`, responsive (no horizontal page scroll), version chip visible, raw JSON toggle works.
* Keep requests via relative `/api/...` or a robust API base.
* Layout rules: keep the wrapper fluid (~95vw), auto-fit KPI cards, and wrap long table cells while leaving IDs/time columns monospace.

### 🔧 SRE/Operator (Ops Agent)

* Owns `appsettings.json`, URL ACL, Windows Service install/upgrade, logs, alerts.
* Guarantees: HTTP prefix is reserved and matches configuration; service can start without admin prompts.

### 📚 Docs/Release (Docs Agent)

* Owns `README.md`, `AGENTS.md`, changelog sections (“What’s new vX.Y”).
* Guarantees: never lose history; always document new options; provide copy-paste commands.

---

## 2) System prompts you can reuse (paste into Codex)

**Core Engineer (Scheduler) – prompt**

```
Act as the Core Engineer for Scheduled Command Executor. Constraints:
- .NET 9.0 BackgroundService on Windows.
- Use Cronos; handle invalid cron without throwing; log once per bad job; skip disabled jobs.
- Compute next‑run with Cronos using UTC base + job TimeZone (DST safe). Implementation note: call `cron.GetNextOccurrence(DateTime.UtcNow, tz)`. Do NOT pass local DateTime to Cronos.
- Concurrency: TryAcquireAsync + Skip (lock) with 0ms duration.
- Per-job timeout kills process tree; treat service shutdown as success/cancel, not a failure.
Task: <describe the exact change here>
Deliver: a full updated C# file or minimal patch + reasoning.
```

**Frontend Engineer (Dashboard) – prompt**

```
Act as the Frontend Engineer. Constraints:
- Single-file dashboard.html (no build step).
- Must work from http://localhost:5058/ and file:// fallback using API_BASE resolution.
- No horizontal page scrolling; keep the wrapper fluid (~95vw); auto-fit KPI cards; wrap long logs/JSON and long table cells, keeping key columns monospace.
- Show version chip from /api/health; Raw JSON toggle.
Task: <UI change>
Deliver: entire updated dashboard.html.
```

**Ops Agent – prompt**

```
Act as SRE/Operator. Goal: make the service reachable at http://localhost:5058/.
- Ensure URL ACL matches the configured prefix, with trailing slash.
- Provide PowerShell commands to add/delete urlacl, and service install commands.
- Verify with curl /api/health.
Task: <deployment target/environment>
```

**Docs Agent – prompt**

```
Act as Docs/Release. Update README.md preserving all history. Add new options and examples. Keep concise copy-paste commands.
Task: bump to version X.Y, summarize changes, add config fields and API examples.
```

---

## 3) Repository map (for orientation)

```
RunCommandsService/
├─ Program.cs                      # Host/DI/config + hot reload
├─ CommandExecutorService.cs       # Scheduler loop + process execution (TZ/DST safe)
├─ ConcurrencyManager.cs           # Keys + parallel run control
├─ SchedulerOptions.cs             # Poll seconds, defaults
├─ Monitoring.cs                   # HttpListener API + serves dashboard.html
├─ dashboard.html                  # Single-file responsive UI
├─ FileLogger.cs                   # Rolling logs
├─ HealthHttpServerService.cs      # (noop placeholder)
├─ appsettings.json                # Configuration (hot-reload)
└─ Logs/                           # Runtime logs
```

---

## 4) Build / Run / Install (CLI)

### Debug run (console)

```powershell
dotnet build -c Debug
dotnet run --project .\RunCommandsService\RunCommandsService.csproj
```

### Reserve URL (run PowerShell as Administrator)

```powershell
# For local debug under your user:
netsh http delete urlacl url=http://localhost:5058/
netsh http add urlacl url=http://localhost:5058/ user="%USERDOMAIN%\%USERNAME%"
```

### Verify API / Dashboard

```powershell
curl http://localhost:5058/api/health
start http://localhost:5058/
```

### Install as a Windows Service (example)

```powershell
# Publish
dotnet publish .\RunCommandsService\RunCommandsService.csproj -c Release -o C:\Apps\RunCommandsService
# Reserve for LocalSystem (if service runs as SYSTEM)
netsh http add urlacl url=http://+:5058/ user="NT AUTHORITY\SYSTEM"
# Install (example using sc.exe)
sc.exe create "ScheduledCommandExecutor" binPath= "C:\Apps\RunCommandsService\RunCommandsService.exe" start= auto
sc.exe start "ScheduledCommandExecutor"
```

---

## 5) Configuration (appsettings.json) – schema & examples

### Minimal schema (job)

```jsonc
{
  "Id": "string",
  "Command": "string",
  "CronExpression": "*/5 * * * *",
  "TimeZone": "UTC | Windows ID | IANA ID",
  "Enabled": true,

  "AllowParallelRuns": false,
  "ConcurrencyKey": "string or null",
  "MaxRuntimeMinutes": 60,

  "AlertOnFail": true,
  "CaptureOutput": true,   // set false for “silent”
  "QuietStartLog": false,
  "CustomAlertMessage": null
}
```

### Service + Dashboard options

```jsonc
{
  "Scheduler": {
    "PollSeconds": 5,
    "DefaultTimeZone": "UTC"
  },
  "Monitoring": {
    "EnableHttpEndpoint": true,
    "HttpPrefixes": [ "http://localhost:5058/" ],
    "Dashboard": {
      "Enabled": true,
      "Title": "Scheduled Command Executor",
      "HtmlPath": "dashboard.html",
      "AutoRefreshSeconds": 5,
      "ShowRawJsonToggle": true
    },
    "AdminKey": "CHANGE-ME"
  }
}
```

### Example jobs

```jsonc
"ScheduledCommands": [
  {
    "Id": "SchedulerSelfTest",
    "Command": "cmd /c \"ver > NUL\"",
    "CronExpression": "*/2 * * * *",
    "TimeZone": "Eastern Standard Time",
    "Enabled": true,
    "AllowParallelRuns": false,
    "ConcurrencyKey": "selftest",
    "MaxRuntimeMinutes": 1
  },
  {
    "Id": "SistemaAutomatizadoDashboard",
    "Command": "cmd /c \"pushd C:\\Apps\\SistemaAutomatizadoDashboard && \\\"%ProgramFiles%\\nodejs\\node.exe\\\" src\\main.js process\"",
    "CronExpression": "40 23 * * 1-5",
    "TimeZone": "America/Santo_Domingo",
    "Enabled": true,
    "AllowParallelRuns": false,
    "ConcurrencyKey": "dashboard",
    "MaxRuntimeMinutes": 60,
    "CaptureOutput": false,
    "QuietStartLog": true,
    "CustomAlertMessage": "Pipeline kicked off; refer to app logs."
  }
]
```

---

## 6) HTTP API (for curl / CLI)

### `GET /api/health`

* Returns:
  `version, nowUtc, recent[], scheduled[], consecutiveFailures{}, ui{ showRawJsonToggle }`

```powershell
curl http://localhost:5058/api/health
```

### `GET /api/logs?tailKb=128`

* Returns the last N KB of the rolling log.

```powershell
curl "http://localhost:5058/api/logs?tailKb=256"
```

### `POST /api/jobs/validateCron` (alias: `/api/jobs/validatecron`)

* Body: `{ "cron": "40 23 * * 1-5", "timeZone": "Eastern Standard Time" }`
* Result: `{ "ok": true, "timeZone": "Eastern Standard Time", "next": [ "2025-...", ... ] }`

```powershell
curl -H "Content-Type: application/json" -d '{ "cron":"*/5 * * * *", "timeZone":"UTC" }' http://localhost:5058/api/jobs/validateCron
# also works (lowercase alias)
curl -H "Content-Type: application/json" -d '{ "cron":"40 23 * * 1-5", "timeZone":"America/Santo_Domingo" }' http://localhost:5058/api/jobs/validatecron
```

### `POST /api/jobs`

* Requires header: `X-Admin-Key: <Monitoring.AdminKey>`
* Body: job JSON (see schema)

```powershell
$body = Get-Content -Raw -Path .\job.json
curl -H "Content-Type: application/json" -H "X-Admin-Key: CHANGE-ME" -d $body http://localhost:5058/api/jobs
```

---

## 7) Cron & Time Zones (runtime contract)

* Cron is **5 fields**: `minute hour day month dayOfWeek` (e.g., `*/5 * * * *`, `0 9 * * *`, `40 23 * * 1-5`).
* Next run is computed with Cronos using a **UTC base time** and the job’s **TimeZone**. Cronos returns a UTC instant that reflects the job’s local wall‑clock (DST‑safe).
  - Engineering rule: Always call `cron.GetNextOccurrence(DateTime.UtcNow, tz)` (or a UTC cursor) and never pass a local `DateTime` to Cronos. Passing local time will throw and/or mis‑schedule.
  - When advancing, compute the next occurrence based on the previous due instant + 1s to avoid drift.
* Invalid `CronExpression` or missing fields:

  * Job is **skipped** and a single error is logged:
    `Job <Id>: invalid CronExpression — <reason>. Job will be skipped until fixed.`
  * Dashboard shows the job; `Next run` empty.

---

## 13) What’s new v2.8 (for agents)

Core Engineer
- Use Cronos TZ API with a UTC base: `cron.GetNextOccurrence(nowUtc, tz)`. Do NOT use the old “fake UTC local wall‑clock” shim.
- Keep the scheduler loop non‑blocking. Dispatch due jobs with `Task.Run` and let `_parallelism` + `ConcurrencyManager` enforce limits.
- On exceptions inside the loop, log and continue; never let a single failure stall other jobs.

Monitoring / API
- `POST /api/jobs/validateCron` uses the same TZ logic as the scheduler. Accept `/api/jobs/validatecron` alias.

Docs Agent
- Update README and changelog when touching time zone or loop behavior. Include curl examples for both endpoints (canonical and alias).

## 8) Common CLI playbooks (copy/paste prompts for Codex)

**Add a new Node job that runs Mon–Fri at 23:40 America/Santo\_Domingo**

```
Add a ScheduledCommands entry:
- Id: "MyApp"
- Command: cmd /c "pushd C:\Apps\MyApp && \"%ProgramFiles%\nodejs\node.exe\" src\main.js process"
- CronExpression: "40 23 * * 1-5"
- TimeZone: "America/Santo_Domingo"
- Enabled: true
- ConcurrencyKey: "myapp"
- MaxRuntimeMinutes: 60
- CaptureOutput: false
- QuietStartLog: true
- CustomAlertMessage: "MyApp started; see its own logs."
Update appsettings.json accordingly and keep JSON valid.
```

**Make a job “silent”** (don’t pipe stdout/err to service log)

```
For job Id "<ID>": set CaptureOutput=false and QuietStartLog=true in appsettings.json.
```

**Prevent overlaps between a daily and a “TestNow” job**

```
Set the same ConcurrencyKey (e.g., "dashboard") on both jobs, and for the test set AllowParallelRuns=false.
```

**Fix 503 on dashboard**

```
Ensure HttpPrefixes includes "http://localhost:5058/" (trailing slash) and recreate urlacl:
netsh http delete urlacl url=http://localhost:5058/
netsh http add urlacl url=http://localhost:5058/ user="%USERDOMAIN%\%USERNAME%"
```

**Cron preview check from CLI**

```powershell
curl -H "Content-Type: application/json" -d '{ "cron":"40 23 * * 1-5", "timeZone":"Europe/Zurich" }' http://localhost:5058/api/jobs/validateCron
```

---

## 9) Troubleshooting checklist

* **Dashboard 503**: prefix mismatch or missing URL ACL. Normalize prefixes (with `/`), reserve URL, restart.
* **“Error loading /api/health” in UI**: open via `http://localhost:5058/` (not file://), or ensure dashboard has robust `API_BASE` fallback.
* **Job never fires**: check `Enabled=true`, valid `CronExpression`, correct `TimeZone`. Use cron preview. Watch for **Skipped (lock)** if concurrency key in use.
* **Logs too noisy**: set `CaptureOutput:false` (and `QuietStartLog:true`) for that job; rely on the app’s own logs.

---

## 10) Conventions

* **C#**: nullable-aware, `await process.WaitForExitAsync(ct)`; kill process tree on timeout; wrap all external calls; never throw out of the loop.
* **Logging**: single error per invalid cron; info on service start/stop; warning on skip/timeouts; errors on failures.
* **Docs**: every release updates README “What’s New” with a concise bullet list and examples.
* **Versioning**: set `<Version>` and `<InformationalVersion>` in csproj; UI shows `version` from `/api/health`.

---

## 11) Release checklist (Docs Agent)

* [ ] Bump csproj Version/InformationalVersion.
* [ ] Update README “What’s New vX.Y” (preserve history).
* [ ] Re-generate `dashboard.html` if UI changed.
* [ ] Confirm dashboard layout on narrow/mobile widths (v2.6 UI check).
* [ ] Smoke test: `/api/health`, cron preview, at least one job firing.
* [ ] Tag and publish.

---

## 12) Glossary

* **NextRunUtc**: The scheduler’s UTC instant for the next execution.
* **NextRunLocal**: Friendly display in the job’s TimeZone; UTC is shown on hover.
* **Skipped (lock)**: Concurrency key held and `AllowParallelRuns=false`.
* **Silent**: `CaptureOutput=false` + `QuietStartLog=true` (child process logs go to its own files).


