# SysPulse — Command Palette Dock Extension Specification

> **Audience:** Claude Code. This document is the single source of truth for building the extension.
> Read it fully before writing code. Where this spec and the official SDK disagree, **the SDK wins** —
> verify every API name against the installed `Microsoft.CommandPalette.Extensions` package and note any deviation in `DECISIONS.md`.

---

## 1. Goal

A PowerToys **Command Palette** extension that contributes a **Dock band** which:

1. Samples system resources (CPU, memory) and processes at a user-configured interval.
2. When a configured threshold is exceeded, re-checks **3 times** at a short, configurable retry interval.
3. If the breach persists through all retries, switches the dock band into a **yellow warning state** and notifies the user.
4. When the user opens the band (flyout), shows the **top 5 processes** currently impacting the system, each with a **Kill** action.
5. When everything is healthy, shows **no warning** — just a compact readout, e.g. `CPU 30% · MEM 50%`.

Working name: **SysPulse** (rename freely; keep IDs consistent).

---

## 2. Platform & SDK baseline

| Item | Requirement |
|---|---|
| Host | PowerToys **0.98+** (Dock shipped in 0.98 / CmdPal 0.9). Target current stable (0.100.x at time of writing). |
| SDK | `Microsoft.CommandPalette.Extensions` **≥ 0.9.260303001** (required for `ICommandProvider3.GetDockBands()`) |
| Toolkit | `Microsoft.CommandPalette.Extensions.Toolkit` (same version line) |
| Language | C# / .NET — use whatever TFM the official extension template generates (do not downgrade) |
| Packaging | MSIX (as produced by the template) |
| OS | Windows 11 x64 (ARM64 build should also compile) |

### 2.1 Scaffolding (step 0)

Do **not** hand-roll the project. Generate it from the official template:
- In Command Palette, run the built-in **"Create extension"** command (or use the template from the PowerToys repo / docs),
- Project name: `SysPulse`, display name: `SysPulse`.
- Then bump the SDK packages to the minimum version above if the template is older.

Reference docs (read before coding):
- Adding Dock support: https://learn.microsoft.com/windows/powertoys/command-palette/adding-dock-support
- Dock user docs: https://learn.microsoft.com/windows/powertoys/command-palette/dock
- Extensibility overview: https://learn.microsoft.com/windows/powertoys/command-palette/extensibility-overview
- Built-in reference implementations in the PowerToys repo (`src/modules/cmdpal/ext/…`): **Time & Date** (`NowDockBand` — live-updating `ListItem` inside a `WrappedDockItem`) and **Performance Monitor** (CPU/memory dock bands). Study both; mirror their patterns.

---

## 3. Key SDK facts this design relies on

- Override `GetDockBands()` in the `CommandProvider` subclass. Each returned `ICommandItem` = one atomic band. Every band item's `Command` **must have a non-empty `Id`**, otherwise it is ignored.
- `WrappedDockItem(IListItem[] items, string id, string displayTitle)` creates a band backed by a `ListPage`; each item renders as a button in that band.
- A band item whose `Command` is an `IListPage` opens that page in a **flyout** when clicked (same UI as the launcher).
- Dynamic content: a `ListItem` can update `Title`, `Subtitle`, `Icon` at runtime (property-change notifications) — this is how `NowDockBand` works.
- Optionally override `GetCommandItem(string id)` (`ICommandProvider4`) so nested commands can be pinned. Not required for v1.

> ⚠️ **Known pitfall (from PowerToys 0.100.2 release notes):** the built-in Performance Monitor band leaked memory by creating new list items on every refresh. **Always reuse stable item instances**; only mutate their properties. Never rebuild the band/page item arrays on each tick.

---

## 4. Architecture

```
SysPulse/
├─ SysPulse.cs                       // IExtension entry (from template)
├─ SysPulseCommandsProvider.cs       // CommandProvider: TopLevelCommands, GetDockBands, Settings
├─ Settings/
│   └─ SysPulseSettings.cs           // settings definitions + persistence (toolkit settings helpers)
├─ Monitoring/
│   ├─ SystemSampler.cs              // system CPU % and memory % (P/Invoke, no PerformanceCounter)
│   ├─ ProcessSampler.cs             // per-process CPU % / memory, delta-based
│   ├─ HealthMonitor.cs              // timer loop + state machine (Normal / Verifying / Alert)
│   └─ Models.cs                     // SystemSnapshot, ProcessSample, HealthState, BreachKind
├─ Dock/
│   └─ StatusDockItem.cs             // ListItem: live "CPU x% · MEM y%" / warning state
├─ Pages/
│   ├─ TopProcessesPage.cs           // ListPage: top 5 processes (flyout + launcher)
│   └─ ProcessListItem.cs            // reusable item per slot (5 fixed slots)
├─ Commands/
│   ├─ KillProcessCommand.cs         // InvokableCommand with confirmation
│   └─ RefreshCommand.cs             // manual refresh
├─ Notifications/
│   └─ AlertNotifier.cs              // optional Windows toast, once per alert episode
├─ Assets/
│   ├─ pulse.svg / .png              // normal icon
│   └─ warning-yellow.svg / .png     // yellow warning icon (fill #FFC400)
└─ DECISIONS.md                      // record any SDK deviations / trade-offs
```

### 4.1 Provider

```csharp
public partial class SysPulseCommandsProvider : CommandProvider
{
    // Create ONCE, keep references for the whole lifetime.
    private readonly HealthMonitor _monitor;
    private readonly StatusDockItem _statusItem;
    private readonly TopProcessesPage _topPage;
    private readonly ICommandItem _dockBand;
    private readonly ICommandItem[] _topLevel;

    public SysPulseCommandsProvider()
    {
        Id = "com.syspulse.extension";
        DisplayName = "SysPulse";
        // Settings = _settings.Settings;   // expose settings page (see §6)

        // _statusItem.Command = _topPage   -> clicking the band opens the Top-5 flyout
        // _dockBand = new WrappedDockItem([_statusItem], "com.syspulse.dockband", "SysPulse");
        // _topLevel = [ new CommandItem(_topPage) { Title = "SysPulse – Top processes" } ];
    }

    public override ICommandItem[] TopLevelCommands() => _topLevel;
    public override ICommandItem[]? GetDockBands() => [_dockBand];
}
```

- The monitor starts when the provider is constructed and stops in `Dispose()`.
- The same `TopProcessesPage` instance is reachable from both the Dock flyout and the launcher top-level command.

---

## 5. Functional behavior

### 5.1 Sampling

**System metrics** (every `ScanIntervalSeconds`):
- **CPU %**: `GetSystemTimes` P/Invoke, delta between two consecutive calls → `(1 - idleDelta / (kernelDelta + userDelta)) * 100`. Do **not** use `PerformanceCounter` (slow init, locale-dependent counter names — the user's machine may be Turkish-locale).
- **Memory %**: `GlobalMemoryStatusEx` → `dwMemoryLoad` (or compute from `ullTotalPhys` / `ullAvailPhys`).

**Process metrics** (every scan, and on-demand when the page is opened):
- Enumerate with `Process.GetProcesses()`.
- Per-process CPU % = `ΔTotalProcessorTime / (ΔwallClock × Environment.ProcessorCount) × 100`, using a cache keyed by **(PID, StartTime)** to survive PID reuse. First observation of a process = 0 % (no delta yet).
- Memory = **Private Bytes** (`PrivateMemorySize64`) as the ranking metric; also capture `WorkingSet64` for display.
- Many processes throw `Win32Exception`/`InvalidOperationException` on `TotalProcessorTime`/`StartTime` (access denied, exited). Catch per process, skip silently, never crash the loop.
- Dispose every `Process` object after reading (they hold handles).
- Prune cache entries for processes that no longer exist.

### 5.2 Thresholds & verification state machine

```
            breach detected
 NORMAL ───────────────────────► VERIFYING (attempt 1..N)
   ▲  ▲                              │   every RetryIntervalSeconds
   │  │  any retry within limits     │
   │  └──────────────────────────────┤
   │                                 │ all N retries still breaching
   │                                 ▼
   │     recovered (below threshold    ALERT  (yellow band + notification)
   └──── − hysteresis) for 1 scan ◄───┘
```

- **Breach** = `CPU% ≥ CpuThreshold` **or** `MEM% ≥ MemoryThreshold` (each metric can be disabled in settings).
- On first breach, switch timer cadence to `RetryIntervalSeconds` and perform `RetryCount` (default **3**) additional checks.
- If **every** retry still breaches (same metric or either metric — record which), enter **ALERT**.
- If any retry is within limits → back to **NORMAL**, resume `ScanIntervalSeconds` cadence. No UI change at all during VERIFYING (the user should never see a flicker).
- In ALERT, keep scanning at `ScanIntervalSeconds`. Leave ALERT only when metrics drop below `threshold − HysteresisPercent` (default 5) for one full scan — prevents flapping around the threshold.
- Record `BreachKind` = `Cpu | Memory | Both` for the UI and for top-5 ranking.
- Thread safety: the timer callback must not overlap itself (use a `PeriodicTimer` loop on a background task or a guard flag). UI property updates happen from that loop; ensure they're marshalled if the SDK requires it (check the Time & Date sample).

### 5.3 Dock band states

| State | Title | Subtitle | Icon |
|---|---|---|---|
| NORMAL | `CPU 30% · MEM 50%` | *(empty or short hint)* | normal pulse icon |
| VERIFYING | same as NORMAL (no visible change) | — | normal |
| ALERT | `⚠ CPU 94% · MEM 52%` | `High CPU` / `High memory` / `High CPU & memory` | **yellow warning icon** (`#FFC400`) |

- Percentages rounded to integers. Only raise property changes when the displayed string actually changes.
- Yellow must come from the icon asset (SVG/PNG with fixed fill), since glyph icons follow theme color. If the Dock turns out to render a colored `Tag` reliably, a yellow tag may be added as a secondary cue — verify visually and record in `DECISIONS.md`.
- Display format should be configurable via setting `CompactLabel` (e.g. `30% | 50%` for narrow/vertical docks).

### 5.4 Notification

- On entering ALERT: send **one** Windows toast (Windows App SDK `AppNotificationManager`) — e.g. *"SysPulse: CPU at 94% for the last ~15 s. Top: chrome.exe (38%)."* Clicking the toast should, if feasible, open Command Palette on the Top-5 page; otherwise just focus nothing.
- No repeated toasts while the episode continues. A new toast only after returning to NORMAL and breaching again.
- Toasts controlled by setting `ShowToast` (default on). If toast registration fails in the MSIX context, log and continue — the yellow band alone is the required minimum.

### 5.5 Top-5 processes page (flyout)

- `ListPage` titled `Top processes`, showing **exactly up to 5** items.
- **Ranking:**
  - ALERT with `Cpu` → sort by CPU % desc.
  - ALERT with `Memory` → sort by Private Bytes desc.
  - `Both` or NORMAL → composite score: `cpuPct / CpuThreshold + memShare / MemoryThreshold` where `memShare = privateBytes / totalPhys × 100`.
- Group by process **name** is *not* done in v1 (each PID is its own row), but add setting `GroupByName` stub in `DECISIONS.md` as a future option.
- Each item:
  - **Title:** process name (`chrome.exe`)
  - **Subtitle:** `CPU 38% · RAM 1.2 GB · PID 12345`
  - **Icon:** the executable's icon if it can be extracted cheaply (try `MainModule.FileName` → icon); fall back to a generic app glyph. Cache icons by path.
  - **Primary command:** `Kill` (see §5.6).
  - **More commands (context menu):** `Open file location`, `Copy PID`.
- Also include a `Refresh` command in the page (and when the page is opened/`GetItems()` is called, trigger a fresh process sample if the last one is older than 2 s).
- **Reuse 5 fixed `ProcessListItem` slots**; update their properties in place. Hide unused slots by returning a shorter array (without re-creating objects).
- When there's nothing notable (NORMAL), the page still shows the top 5 — the user asked for it on open regardless of state.

### 5.6 Kill action

- `KillProcessCommand : InvokableCommand`, bound to a PID + StartTime (verify the process identity is unchanged before killing).
- Ask for **confirmation** before killing (use the toolkit's confirmation result, e.g. `CommandResult.Confirm(...)`, with text `Kill chrome.exe (PID 12345)?`). Setting `ConfirmKill` (default on).
- Kill with `Process.Kill(entireProcessTree: false)`. Setting `KillProcessTree` (default off).
- **Protected list — never offer Kill** (show the item without the kill command, subtitle suffix `· protected`):
  `System`, `Idle`, `Registry`, `smss`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `svchost`, `dwm`, `fontdrvhost`, `Memory Compression`, `MsMpEng`, plus the Command Palette host process and SysPulse's own process. Keep this list in one place and make it extendable via setting `ProtectedProcesses` (comma-separated).
- On failure (access denied because the target is elevated, already exited, etc.) show a status/toast message with the reason; don't throw. Note: the extension runs un-elevated, so elevated processes can't be killed — say so in the error text.
- After a kill, refresh the page immediately.

---

## 6. Settings

Expose via the provider's `Settings` using the toolkit settings helpers (look at how the template / built-in extensions define and persist settings, e.g. a `JsonSettingsManager` subclass with typed settings). Validate and clamp values on load.

| Key | Type | Default | Range / notes |
|---|---|---|---|
| `ScanIntervalSeconds` | number | 10 | 2 – 300 |
| `RetryIntervalSeconds` | number | 3 | 1 – 60, must be < ScanInterval |
| `RetryCount` | number | 3 | 1 – 10 (spec requirement is 3; keep configurable) |
| `CpuMonitoringEnabled` | toggle | on | |
| `CpuThreshold` | number (%) | 85 | 10 – 100 |
| `MemoryMonitoringEnabled` | toggle | on | |
| `MemoryThreshold` | number (%) | 90 | 10 – 100 |
| `HysteresisPercent` | number | 5 | 0 – 20 |
| `ShowToast` | toggle | on | |
| `ConfirmKill` | toggle | on | |
| `KillProcessTree` | toggle | off | |
| `CompactLabel` | toggle | off | `30% \| 50%` format |
| `ProtectedProcesses` | text | *(empty)* | extra names, comma-separated, case-insensitive, `.exe` optional |

- Changes take effect **without restart**: the monitor subscribes to a settings-changed event and restarts its timer with new values.

---

## 7. Non-functional requirements

- **Overhead budget:** the extension itself should stay < 1 % average CPU and < 60 MB private bytes at default settings. Measure with the extension's own process in Task Manager during a 10-minute run; record results in `DECISIONS.md`.
- **No allocations growth:** run 30 minutes at `ScanIntervalSeconds = 2`; private bytes must stay flat (guards against the known dock refresh leak pattern).
- **Resilience:** any exception inside a sampling cycle is caught and logged; the loop continues on the next tick.
- **Logging:** lightweight file log under the package's LocalState folder (rolling, max ~1 MB). Debug-level off by default.
- **Localization:** all user-facing strings in a `.resw` resource file; ship **en-US** and **tr-TR**.
- **Code style:** nullable enabled, async where I/O, no blocking calls on the UI/property-change path, `sealed` where appropriate, XML doc comments on public types.

---

## 8. Testing

Create a `SysPulse.Tests` project (xUnit or MSTest — match whatever the template/repo leans toward).

Unit-test (with injected fake sampler + fake clock):
1. Single spike that recovers on retry 1 → stays NORMAL, no UI change.
2. Breach persisting through all 3 retries → ALERT, exactly one toast.
3. ALERT → metric at `threshold − 2` (inside hysteresis) → stays ALERT.
4. ALERT → metric at `threshold − 6` → NORMAL.
5. Both metrics disabled → never alerts.
6. CPU % delta calculation, including PID reuse (same PID, different StartTime → treated as a new process).
7. Ranking: CPU-only, memory-only, composite.
8. Protected list: kill command absent; name matching case-insensitive and with/without `.exe`.
9. Settings clamp invalid values (e.g. RetryInterval ≥ ScanInterval).

Manual test script (write as `TESTING.md`):
- Stress CPU (e.g. a PowerShell loop per core) → band turns yellow after ~ScanInterval + 3×RetryInterval; top-5 shows the stressing process first; kill it → band returns to normal after next scan.
- Stress memory with a test allocator app → same flow for memory.
- Vertical Dock (left/right edge) and label-off mode render acceptably.
- Try killing an elevated process → friendly error, no crash.

---

## 9. Implementation order (for Claude Code)

1. Scaffold from template, bump SDK, confirm the extension loads in Command Palette.
2. `SystemSampler` + unit tests.
3. Minimal dock band showing live `CPU x% · MEM y%` (reusing a single `ListItem`). Deploy, pin to Dock, verify live updates.
4. `HealthMonitor` state machine + tests (fake clock).
5. ALERT visuals (yellow icon, title/subtitle).
6. `ProcessSampler` + `TopProcessesPage` with 5 reused slots.
7. `KillProcessCommand` with confirmation + protected list.
8. Settings page + live reload.
9. Toast notifier.
10. Localization (en-US, tr-TR), logging, `TESTING.md`, overhead/leak measurement.

After each step: build, deploy (Debug MSIX), reload extensions in Command Palette, and verify before moving on. Commit per step with a clear message.

---

## 10. Out of scope (v1)

- GPU, disk, network metrics (the built-in Performance Monitor covers those).
- Historical graphs.
- Killing elevated processes / self-elevation.
- Per-monitor different thresholds.

## 11. Open points to confirm with the user if encountered

- Exact wording/format of the dock label if the Dock truncates it at the chosen width.
- Whether "top 5" should group multi-process apps (Chrome, Edge, Teams) by name.
- Whether the alert should also trigger on sustained per-process usage (e.g. a single process > 50 % CPU) even if system totals are under threshold.
