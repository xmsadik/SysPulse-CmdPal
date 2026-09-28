# SysPulse — Manual Test Script

Companion to the automated suite in `SysPulse\SysPulse.Tests`. Run this after any change that
touches monitoring, the Dock band, the Top‑5 flyout, settings, localization, or logging, and
before every release. Mirrors spec §8 plus the H1–H3 polish pass (localization, logging,
this document).

## 0. Dev loop

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy.ps1
```

This stops any running `SysPulse.exe`, builds the MSIX project (`x64`, `Debug` by default), and
registers the loose-layout package with `Add-AppxPackage -Register -ForceUpdateFromAnyVersion`.
After it finishes, open **Command Palette** and run **Reload** (or fully restart Command Palette)
so the host picks up the new build — registering the package alone does not reload an
already-running extension host.

Useful variants:

```powershell
# Release build
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Configuration Release

# Skip the build, just re-register the last-built output (e.g. after only editing a .resx)
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -NoBuild
```

**Do not** pass `-Platform ARM64` while another build of that platform may be running
concurrently on this machine — `x64` is the platform this test script (and the task that produced
it) targets exclusively.

### Unit tests

```powershell
dotnet test SysPulse\SysPulse.Tests\SysPulse.Tests.csproj -p:Platform=x64
```

Expect **0 failed**, **0 warnings** during the build step. As of this pass: 140 tests (state
machine, ranking, formatting, settings clamping, `FileLogger`, and the tr‑TR resource spot-checks
in `SysPulse.Tests\Formatting\LocalizationTests.cs`).

### Where things live at runtime

| What | Path |
|---|---|
| Settings (JSON, keys unchanged by this pass) | `%LOCALAPPDATA%\Packages\SysPulse_8wekyb3d8bbwe\LocalState\settings.json` |
| Log file (rolling, ~1 MB, one `.1.log` backup) | `%LOCALAPPDATA%\Packages\SysPulse_8wekyb3d8bbwe\LocalState\SysPulse\logs\syspulse.log` |
| Deployed loose MSIX (x64, Debug) | `SysPulse\SysPulse\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\` |
| tr-TR satellite resource assemblies | `...\win-x64\tr-TR\SysPulse.resources.dll` and `...\win-x64\tr-TR\SysPulse.Core.resources.dll` |

The package family name (`SysPulse_8wekyb3d8bbwe`) comes from the unsigned dev certificate in
`app.manifest`/the packaging project; confirm it on your machine with
`Get-AppxPackage | Where-Object Name -like '*SysPulse*'` if it ever differs.

Tail the log live while testing:

```powershell
Get-Content "$env:LOCALAPPDATA\Packages\SysPulse_8wekyb3d8bbwe\LocalState\SysPulse\logs\syspulse.log" -Wait -Tail 20
```

Debug-level entries are compiled out of Release builds (`#if DEBUG` in
`SysPulseCommandsProvider`'s constructor — see `SysPulse\SysPulse\Logging\Log.cs`); Info/Warning/
Error are always on, and the host never logs on every sampling tick (only alert edges, start/stop,
settings changes, kill attempts/results, toast failures, and sampling faults — keeps the ~1 MB
rolling file from filling up on its own).

---

## 1. CPU stress

Pin the SysPulse band to the Dock first (Command Palette → Dock settings, or drag from the
launcher). Open a PowerShell window per test run:

```powershell
# Loads every logical core to ~100% until you stop it (Ctrl+C), or run the timed version below.
$jobs = 1..[Environment]::ProcessorCount | ForEach-Object {
    Start-Job { while ($true) { } }
}
# ... watch the Dock band ...
$jobs | Stop-Job; $jobs | Remove-Job
```

Timed variant (stops itself after N seconds — useful for a hands-off run):

```powershell
param([int]$Seconds = 60)
$deadline = (Get-Date).AddSeconds($Seconds)
$jobs = 1..[Environment]::ProcessorCount | ForEach-Object {
    Start-Job { param($until) while ((Get-Date) -lt $until) { } } -ArgumentList $deadline
}
Wait-Job $jobs -Timeout ($Seconds + 5) | Out-Null
$jobs | Stop-Job; $jobs | Remove-Job
```

**Expected timing** (default settings: `ScanIntervalSeconds=10`, `RetryIntervalSeconds=3`,
`RetryCount=3`, `CpuThreshold=85`):

- A breach is first *detected* within one `ScanIntervalSeconds` (≤ 10 s) of CPU crossing 85%.
- The band then enters `Verifying` and re-checks every `RetryIntervalSeconds` (3 s). It takes
  `RetryCount` (3) consecutive breaching retries to confirm — so **worst case, the band turns
  yellow ~`ScanIntervalSeconds + RetryCount × RetryIntervalSeconds` = 10 + 3×3 = ~19 s** after the
  stress starts. Best case (breach caught right at the end of a scan tick) is closer to
  `RetryCount × RetryIntervalSeconds` = ~9 s.
- A single short spike that recovers before all `RetryCount` retries breach must **not** alert
  (state returns to `Normal` — this is covered by `HealthMonitorTests` already, but worth eyeballing).
- Once alerted: dock title/subtitle show the two-line `CPU 82%` / `MEM 30% · High CPU` label (or
  the single-line `82% | 30%` + `High CPU` if `CompactLabel` is on — DECISIONS.md D11), the icon
  turns yellow (`Assets\warning-yellow.svg`), and — if `ShowToast` is on — **exactly one** Windows
  toast appears for this alert episode (see §5).
- Open the flyout (click the band, or the launcher's **SysPulse – Top processes** /
  **En çok kaynak kullanan işlemler** command): the stressing `powershell.exe`/`pwsh.exe`
  processes should be at or near the top of the Top‑5 list, ranked by CPU% descending.
- Stop the stress (`Ctrl+C` or the `Stop-Job` line above). Within one more `ScanIntervalSeconds`,
  the metric drops below `CpuThreshold − HysteresisPercent` (85 − 5 = 80%) and the band returns to
  `Normal` (icon back to `Assets\pulse.svg`, alert reason cleared). Check `syspulse.log` for the
  matching `Alert entered:` / `Alert left:` pair with the cpu/mem values recorded.

## 2. Memory stress

```powershell
param(
    [double]$TargetPercent = 92,   # stop allocating once system-wide memory usage reaches this %
    [int]$ChunkMB = 256
)
$totalBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$chunks = [System.Collections.Generic.List[byte[]]]::new()
while ($true) {
    $used = (Get-Counter '\Memory\% Committed Bytes In Use').CounterSamples[0].CookedValue
    if ($used -ge $TargetPercent) { break }
    $chunks.Add([byte[]]::new($ChunkMB * 1MB))  # touch pages so they're actually committed
    for ($i = 0; $i -lt $chunks[$chunks.Count - 1].Length; $i += 4096) { $chunks[$chunks.Count-1][$i] = 1 }
    Start-Sleep -Milliseconds 200
}
Write-Host "Holding ~$($chunks.Count * $ChunkMB) MB. Press Enter to release."
Read-Host
$chunks.Clear()
[System.GC]::Collect()
```

Same expected flow as CPU stress (§1), against `MemoryThreshold` (default 90%) instead. **Always
run the cleanup** (`Read-Host` then `$chunks.Clear(); [System.GC]::Collect()`, or just close the
PowerShell window) before moving on — leftover held memory will keep the band in `Alert` and
pollute the next test.

## 3. Kill flow

1. From the Top‑5 flyout, invoke the primary command (Enter) on a disposable process (e.g. a
   `notepad.exe` you started yourself). With `ConfirmKill` on (default), a confirmation dialog
   appears: **Kill process** / **İşlemi sonlandır**, body **`Kill {name} (PID {pid})?`** /
   **`{name} (PID {pid}) sonlandırılsın mı?`**.
2. Confirm. Expect a status toast **`{name} killed.`** / **`{name} sonlandırıldı.`** and the list
   refreshes (the process drops out of Top‑5 within one refresh cycle).
3. Toggle `ConfirmKill` off in Settings and repeat — the kill should happen immediately, no dialog.
4. Try killing a process that already exited between opening the flyout and confirming (e.g. close
   it manually first, then confirm the stale dialog): expect **`{name}: process already exited.`**
   / **`{name}: işlem zaten sona ermiş.`**, no crash.
5. Check `syspulse.log` for a `Kill requested:` line followed by the matching result line
   (`Kill succeeded:` / `Kill skipped:` / a `Warning`/`Error` line) for each attempt above.

## 4. Elevated kill

```powershell
Start-Process notepad -Verb RunAs
```

Find it in the Top‑5 list (you may need to raise its CPU/memory footprint, or just lower
`CpuThreshold`/`MemoryThreshold` temporarily in Settings so it shows up, or use **Refresh** —
elevated processes are still sampled and listed, per spec §5.1/D9, `NtQuerySystemInformation`
needs no handle). Attempt to kill it:

- Expect a friendly warning toast: **`Cannot kill {name}: it runs with administrator rights and
  SysPulse runs unelevated.`** / **`{name} sonlandırılamıyor: yönetici yetkisiyle çalışıyor,
  SysPulse yetkisiz çalışıyor.`** — no crash, no unhandled exception.
- `syspulse.log` should show a `Warning` line: `Kill denied (access denied / elevated): ...`.
- Close the elevated Notepad manually afterwards.

## 5. Protected processes

1. In Settings, confirm the built-in protected list covers OS/session processes and SysPulse's own
   host processes (`System`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `svchost`, `dwm`,
   `explorer`, `MsMpEng`, `Microsoft.CmdPal.UI`, `PowerToys`, `SysPulse`, ...) — none of these
   should ever offer a **Kill**/**Sonlandır** primary command; their row's subtitle ends with
   **`· protected`** / **`· korumalı`** instead, and **Copy PID**/**PID'yi kopyala** is the primary
   command for that row instead.
2. Add a custom name to **"Never offer Kill for these processes"** /
   **"Bunlar için Sonlandır önerilmesin"** (e.g. `notepad`, or `notepad.exe` — both must match,
   case-insensitively) and Save. Start that process, refresh the flyout: it now also shows
   `· protected` and offers no Kill.

## 6. Settings live reload

With the flyout or Dock band open (so the monitor loop is running), change each of these in
**Settings** and confirm the effect appears **without restarting the extension**:

- `CompactLabel` toggle → dock label immediately switches between the two-line `CPU x%` / `MEM y%`
  form and the single-line `x% | y%` form (check both a healthy and an active-alert state).
- `CpuThreshold` / `MemoryThreshold` → lower one below the current live value; the band should
  enter `Verifying`/`Alert` on the next scan/retry tick without needing a reload.
- `RetryIntervalSeconds` clamp: type a value ≥ the current `ScanIntervalSeconds` (e.g. equal to
  it) and Save — the settings card should show the value silently corrected down to
  `ScanIntervalSeconds − 1` (D12's write-back behavior; also covered by
  `SysPulseOptionsTests`/`SettingParsersTests`, but confirm the *UI* reflects it, not just the
  underlying option).
- **Persistence**: after changing several settings, fully restart Command Palette (not just
  Reload) and re-open SysPulse Settings — every changed value must still be there, and
  `%LOCALAPPDATA%\Packages\SysPulse_8wekyb3d8bbwe\LocalState\settings.json` should show the same
  values (keys are unchanged by this localization/logging pass — do not expect new/renamed JSON
  keys).
- `syspulse.log` should show one `Settings changed: ...` line per Save.

## 7. Toast — once per alert episode

1. With `ShowToast` on, trigger an alert (§1 or §2). Confirm **exactly one** toast appears for
   that episode (not one per retry, not one per scan tick while still in `Alert`).
2. While still in `Alert`, wait through several more scan ticks — no additional toast should
   appear.
3. Let it recover to `Normal`, then trigger a second, separate alert episode — a **new** toast
   should appear for this one (tag/group `syspulse-alert`/`syspulse` replaces rather than stacks
   with the first, per D10, so you may only ever see the latest one in Action Center — that's
   expected).
4. If toast display ever fails (e.g. notification permission revoked in Windows Settings), expect
   no crash — the extension disables further toasts for the rest of the process's life and logs a
   `Warning: Toast display failed ...` line once.

## 8. Vertical Dock and label-off mode

1. In PowerToys Dock settings, move the Dock to the **left** or **right** screen edge (vertical
   orientation). Confirm the SysPulse band still renders without visual corruption; the
   `CompactLabel` single-line `x% | y%` format is the intended fit for this layout — turn it on
   and compare against the default two-line format.
2. In PowerToys Dock settings, enable whatever "hide labels" / icon-only display mode the Dock
   host offers for narrow layouts. Confirm the band still shows a sensible icon-only state (normal
   pulse icon vs. yellow warning icon) and that clicking it still opens the Top‑5 flyout correctly
   — SysPulse does not control this mode itself, it only needs to not break under it.

## 9. tr-TR UI check

CmdPal/SysPulse resource lookups key off `CurrentUICulture` (task requirement H1), which .NET
seeds from the **process's OS UI language** at startup — there is no in-app language switch.

1. Windows Settings → **Time & Language → Language & region** → add/select **Türkçe (Türkiye)**
   and set it as the **Windows display language** (this requires a language pack download and,
   typically, a sign-out/sign-in to fully apply to new processes).
2. Sign out and back in (or at minimum, fully quit Command Palette / kill the `SysPulse.exe`
   COM-server process and any running CmdPal host process, then relaunch) so the extension host
   process picks up the new OS UI language from a cold start.
3. Re-check:
   - Dock alert reasons: **Yüksek CPU** / **Yüksek bellek** / **Yüksek CPU ve bellek**.
   - Top‑5 flyout title **En çok kaynak kullanan işlemler**, empty state
     **Henüz işlem verisi yok** / **İşlemler örnekleniyor…**.
   - Context commands: **Sonlandır**, **Dosya konumunu aç**, **PID'yi kopyala**, **Yenile**.
   - Kill confirm dialog: **İşlemi sonlandır** / **`{0} (PID {1}) sonlandırılsın mı?`**.
   - Settings page: every label/description in Turkish (see `SysPulse\SysPulse\Properties\Resources.tr-TR.resx`
     for the full list).
   - Technical tokens stay as-is in both languages: `CPU`, `MEM`/`RAM`, `PID`, `%` — e.g.
     **`CPU %94 seviyesinde`**, not a translated "CPU" token.
4. Switch the display language back to English and repeat step 2 to restore the default state for
   further testing.

If a full language-pack install isn't practical on the test machine, the two automated spot-checks
in `SysPulse.Tests\Formatting\LocalizationTests.cs` (`BuildBody_TrTrUiCulture_ReturnsTurkishText`,
`Format_ProtectedProcess_TrTrUiCulture_AppendsTurkishProtectedSuffix`) exercise the same resource
lookups in-process by setting `CultureInfo.CurrentUICulture` directly — run those as a lighter
substitute, and additionally sanity-check the compiled satellite assemblies exist:

```powershell
Get-ChildItem "SysPulse\SysPulse\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\tr-TR" -Filter *.resources.dll
```

Both `SysPulse.resources.dll` and `SysPulse.Core.resources.dll` must be present.

## 10. Overhead / leak spot-check (spec §7, not re-run every time)

With the band pinned and healthy (no active alert), watch `SysPulse.exe` in Task Manager for
10 minutes: average CPU should stay under ~1%, private bytes under ~60 MB. For a longer leak
check, run 30 minutes at `ScanIntervalSeconds = 2` and confirm private bytes stay flat rather than
climbing tick over tick.
