# DECISIONS

SDK deviations, trade-offs and measurements for SysPulse. Research details and sources: `docs/sdk-research.md`.

## D1 — SDK version: `Microsoft.CommandPalette.Extensions` 0.12.260812002
- Template generated 0.9.260303001 (spec minimum). Installed host (PowerToys 0.101 / CmdPal 0.12) ships SDK **0.12.260824005**, so the latest nuget.org release (0.12.260812002) is safe and gives access to `DockLabelWidth` APIs.
- The Toolkit is **not** a separate package; `Microsoft.CommandPalette.Extensions.Toolkit.dll` ships inside the same package (spec §2 table lists it separately).

## D2 — Project split: `SysPulse` + `SysPulse.Core` + `SysPulse.Tests`
- All SDK-independent logic (samplers, state machine, ranking, protected list, options validation) lives in `SysPulse.Core` so xUnit tests don't reference the MSIX/COM-server project. Spec §4 tree shows a single project.

## D3 — Monitor runs only while the band is on screen (lifecycle gating)
- The host constructs the provider and its bands even when nothing is pinned. The host subscribes to a band page's `ItemsChanged` when the band is rendered and unsubscribes when it goes away; that is the only load signal.
- SysPulse ports `OnLoadDynamicListPage` / `OnLoadDockBandItem` (MIT, from PowerToys `Microsoft.CmdPal.Ext.TimeDate`) and starts/stops the monitor loop from those callbacks (ref-counted with the Top-5 page being open). Spec §4.1 said "monitor starts when the provider is constructed" — replaced by this.
- Consequence: with the band unpinned, no background sampling and no toasts.

## D4 — Threading
- Confirmed (TimeDate `NowDockBand`): item properties are set directly from a timer thread; no dispatcher. Properties set only when the string changes. Every set wrapped in try/catch (issue #50483: a throwing host handler blocked later updates).

## D5 — Settings: no numeric setting type
- Toolkit offers Text/Toggle/ChoiceSet only. Numeric keys use `TextSetting` + parse + clamp (`SysPulseOptions.Normalize`).

## D6 — Localization uses `.resx`
- Built-in CmdPal extensions (e.g. TimeDate) use `.resx` + `PublicResXFileCodeGenerator`. Spec §7 says `.resw`.

## D7 — Dock label width
- Use the SDK's `DockLabelWidth` / `SetDockLabelReservations` (as Performance Monitor does) to prevent width jitter. `CompactLabel` setting is kept as the spec requires, for narrow/vertical docks.
- **Verified unavailable in the installed package (2026-09-28):** `Microsoft.CommandPalette.Extensions.Toolkit.dll` from NuGet `Microsoft.CommandPalette.Extensions` 0.12.260812002 has no `DockLabelWidth`/`DockLabelWidthExtensions`/`DockLabelPresentationExtensions` types at all (confirmed via `System.Reflection.PortableExecutable.PEReader` type-table dump of the DLL — zero matches for `DockLabel*`, vs. `WrappedDockItem`/`IExtendedAttributesProvider` which are present). The PowerToys `main`-branch source docs/sdk-research.md cites for this API post-dates this NuGet release. `StatusDockItem` therefore does **not** call these APIs; `Dock\StatusDockItem.cs` documents this in a code comment. Same finding applies to `EventHelpers` (used by upstream `OnLoadDynamicListPage.RaiseItemsChanged`): absent from the installed Toolkit DLL, so the ported `SysPulse.Dock.OnLoadDynamicListPage` invokes its internal event delegate directly instead.

## D8 — Band shape
- Single status item whose `Command` is `TopProcessesPage` (click → flyout), hosted by the ported `OnLoadDockBandItem` instead of `WrappedDockItem` (needed for D3).

## D9 — Process sampling via `NtQuerySystemInformation` (planned, D-phase)
- One call returns all processes (PID, name, create time, CPU times, private bytes, working set) without opening handles; elevated processes are included. Spec §5.1 prescribes `Process.GetProcesses()`.

## D10 — Toast via WinRT `ToastNotificationManager` (G-phase, implemented)
- `SysPulse\Notifications\AlertNotifier.cs` implements `IAlertNotifier`. `NotifyAlert` returns immediately (`Task.Run`); all work — process sampling and toast display — runs on a background task and never throws back into `MonitorLoop`; every failure is logged via `Debug.WriteLine`.
- Confirmed `Windows.UI.Notifications` / `Windows.Data.Xml.Dom` are reachable directly from the `net10.0-windows10.0.26100.0` TFM with no extra package reference (same as the already-used `Windows.Foundation` in `Dock\OnLoadDynamicListPage.cs`) — no COM activator/`AppNotificationManager` needed, matching the plan.
- Toast body built via `ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02)`, with `text` node values inserted through `XmlDocument.CreateTextNode` (DOM API), not string concatenation — the DOM serializer escapes automatically, so a process name containing `&`/`<`/`>` cannot break the markup.
- `ToastNotificationManager.CreateToastNotifier()` (packaged identity, no AUMID) and `.Show(toast)` are wrapped in one try/catch; on failure (identity/policy) the exception is logged once and a `_toastDisabledForSession` flag suppresses all further toast attempts for the life of the process (re-armed on next process start/reload).
- `Tag = "syspulse-alert"`, `Group = "syspulse"` on every `ToastNotification` so a new alert episode replaces the previous toast instead of stacking. No click-to-open (no host API to jump to the Top-5 page from outside CmdPal) — clicking the toast just dismisses it, per this decision's original scope.
- Message formatting (title `"SysPulse"`, body e.g. `"CPU at 94% for the last ~9 s. Top: chrome.exe (38%)."`) is pure code in `SysPulse.Core\Formatting\AlertMessageFormatter.cs`, unit-tested in `SysPulse.Tests\Formatting\AlertMessageFormatterTests.cs` (Cpu, Memory, Both, no-top-process, invariant-culture cases). The duration shown is `RetryCount × RetryIntervalSeconds` (seconds) from the options in effect *at alert time*, prefixed with `~` since it's an estimate, not a measured duration.
- Top process: `AlertNotifier` owns a **dedicated** `ProcessSampler` instance (not shared with `TopProcessesPage`, which isn't thread-safe). For `Cpu`/`Both` it takes two samples ~1 s apart (CPU% needs a delta) and ranks via `ProcessRanker.Top(samples, breachKind, HealthState.Alert, options, snapshot.TotalPhysBytes, 1)`; for `Memory` one sample suffices. The protected-process list is *not* applied here — the real top consumer is always shown. For `Both`, the top-process value shown is CPU% (the dominant signal for a mixed breach); for `Memory` it's the formatted byte size (`ByteFormatter`). If sampling throws, the "Top:" clause is simply omitted.
- Package.appxmanifest: no changes needed. Plain `Windows.UI.Notifications` toasts (no actions, no background activation, no scheduled/push notifications) need no extra `<Extensions>`/capability declaration beyond the package `Identity` the manifest already has.
- Wired in `SysPulseCommandsProvider`: `new AlertNotifier(() => _options)` passed to `MonitorLoop`'s `notifier` parameter; disposed (releasing its `ProcessSampler`) in `Dispose()` alongside the provider's own `_processSampler`.
- AOT/trim: `AlertNotifier` is declared `sealed partial` — required by the CsWinRT source generator (`CsWinRT1028`) for any type that touches WinRT interop types, to keep the build warning-free under `IsAotCompatible`. Build is 0 warnings / 0 errors (`dotnet build SysPulse\SysPulse\SysPulse.csproj -p:Platform=x64`).

## Future options
- `GroupByName` (group multi-process apps in Top-5) — not in v1.

## D11 — Two-line dock label, no "⚠" in text (user decision, 2026-09-28)
- The Dock truncated `⚠ CPU 82% · MEM 93%` to `CPU 82% · M…`. Resolves spec §11 open point 1.
- Title = `CPU 82%`, Subtitle = `MEM 93%` (on alert: `MEM 93% · High memory`). `CompactLabel` = single title `82% | 93%`, alert reason in subtitle.
- The yellow icon is the alert cue; the `⚠` prefix from spec §5.3 is dropped (it was shown twice and cost width).

## D12 — Settings phase: `SysPulseSettingsManager`, `Utilities.BaseSettingsPath` confirmed, reachability
- Verified against the installed `Microsoft.CommandPalette.Extensions.Toolkit.dll` (AssemblyLoadContext reflection, same technique as D7) and against `Microsoft.CmdPal.Ext.TimeDate.Helpers.SettingsManager` / `Microsoft.CmdPal.Ext.PerformanceMonitor.SettingsManager` (`gh api .../contents/...`): `JsonSettingsManager` (`Settings`, `FilePath { get; set; }`, `LoadSettings()`, `SaveSettings()`), `Settings.Add<T>`/`SettingsChanged`/`SettingsPage`, `ToggleSetting`/`TextSetting` (both have a `(key, defaultValue)` and a `(key, label, description, defaultValue)` ctor), and `Utilities.BaseSettingsPath(string)` all exist exactly as `docs/sdk-research.md` §4 described from the PowerToys `main`-branch source — resolves that doc's "UNVERIFIED" note on `BaseSettingsPath`. `Utilities.BaseSettingsPath("SysPulse")` returns the packaged app's redirected `LocalState` folder (confirmed via `Utilities.cs` source: `IsPackaged()` short-circuits the fallback-folder-name branch), so SysPulse's settings file lives at `%LOCALAPPDATA%\Packages\<SysPulse PFN>\LocalState\settings.json` once sideloaded — no direct `ApplicationData.Current.LocalFolder` WinRT call needed.
- `SysPulseCommandsProvider.Settings = _settingsManager.Settings;` (base `CommandProvider.Settings` is `ICommandSettings`; `Settings` implements it) — confirmed compiling and matches `TimeDateCommandsProvider`'s exact pattern.
- **Settings reachability**: nothing in the installed SDK auto-adds a "Settings" entry anywhere. `TimeDateCommandsProvider` proves the pattern built-ins use: add `MoreCommands = [new CommandContextItem(_settingsManager.Settings.SettingsPage)]` to a top-level command, in addition to assigning the provider's `Settings` property (which is what feeds the *host's own* "Extensions" settings surface, e.g. CmdPal's own Settings → Extensions page — not verified visually in this pass, no interactive CmdPal session available). SysPulse mirrors this exactly on its one top-level command ("SysPulse – Top processes").
- Numeric keys (`ScanIntervalSeconds`, `RetryIntervalSeconds`, `RetryCount`, `CpuThreshold`, `MemoryThreshold`, `HysteresisPercent`) are `TextSetting`s parsed by the new `SysPulse.Core.Settings.SettingParsers.ParseNumber(string?, double)`: tries `double.TryParse` with `InvariantCulture` first, then `CurrentCulture` (so a tr-TR user typing `2,5` parses correctly), else returns the caller-supplied fallback (the matching `SysPulseOptions` default) — never throws. `SysPulseSettingsManager.ToOptions()` builds a raw `SysPulseOptions` from every setting's current value, calls `.Normalize()` (existing `SysPulseOptions.Clamp`/RetryInterval-vs-Scan logic), and writes any corrected numeric value back into its owning `TextSetting.Value` so the settings card shows the effective value on next render; a `_isApplyingNormalization` guard flag brackets the write-back (defensive — the installed `Settings.cs` source shows a direct `Setting<T>.Value` assignment does not itself raise `SettingsChanged`, so no loop actually occurs today, but the guard documents the invariant against a future toolkit change).
- `SysPulseCommandsProvider._options` changed from a `readonly` field to `private volatile SysPulseOptions _options;` so `OnSettingsChanged` can publish a new value visible to every accessor-based consumer (`TopProcessesPage`, `ProcessListItem`, `KillProcessCommand` — all already read options via an injected `Func<SysPulseOptions>` closing over this field, so no consumer-side changes were needed). `OnSettingsChanged` also calls `_monitorLoop.UpdateOptions(...)`, `RebuildProtectedProcessList()`, and re-invokes `StatusDockItem.Apply` with the last `(HealthEvaluation, SystemSnapshot)` pair (tracked in two new fields under a dedicated lock, updated in `OnSampled`) so a toggle like `CompactLabel` or `ShowToast` is reflected on the dock band immediately, without waiting for the next scan tick.

## D14 — ARM64 build (2026-09-28)
- `dotnet build -p:Platform=ARM64 -c Release` succeeds. Remaining 3 warnings are third-party: IL2104 trim warnings inside `Microsoft.Windows.SDK.NET` / `WinRT.Runtime`, and the template's APPX1707 (winmd without implementation). None originate in SysPulse code.
