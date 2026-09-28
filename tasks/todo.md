# SysPulse — Uygulama Planı (v2 — Fable critique sonrası)

Kaynak: `SysPulse-CmdPal-Dock-Extension-Spec.md` (tek doğruluk kaynağı; SDK ile çelişirse SDK kazanır → `DECISIONS.md`).

## 0. Ortam tespiti (2026-09-28)

| Öğe | Durum |
|---|---|
| PowerToys | **0.101 (Preview)** kurulu → Dock mevcut ✅ |
| .NET SDK | 10.0.401 ✅ |
| Build araçları | VS Build Tools 2022 17.14 (tam VS IDE yok) — MSIX derleme CLI'dan; "MSIX Packaging Tools" + Windows SDK bileşenleri doğrulanmalı |
| Developer Mode | Açık ✅ (`Add-AppxPackage -Register` ile loose deploy) |
| Git | Kurulu, **klasör repo değil** → `git init` gerekli |

## 1. Mimari kararlar (→ `DECISIONS.md`)

1. **Proje ayrımı:** `SysPulse` (MSIX, CmdPal SDK) + **`SysPulse.Core`** (saf class library: samplers, `HealthMonitor`, ranking, protected list, `SysPulseOptions` + clamp) + `SysPulse.Tests` (xUnit → Core). Gerekçe: MSIX projesini test projesinden referanslamak kırılgan.
2. **Zaman soyutlaması:** `TimeProvider` (+ testte `FakeTimeProvider`). `HealthMonitor` = saf durum makinesi (`Evaluate(snapshot) → Transition`) + ayrı döngü sürücüsü (`PeriodicTimer`, retry cadence için `Period` değiştirilir, timer yeniden yaratılmaz).
3. **Arayüzler:** `ISystemSampler`, `IProcessSampler`, `IAlertNotifier`, `IProcessKiller` → fake'lerle test.
4. **UI modeli:** tek `StatusDockItem` + tek `TopProcessesPage` + 5 sabit `ProcessListItem`; tick'te yalnızca property mutasyonu, string değişmediyse set yok. Her property set `try/catch` içinde (host tarafı handler exception'ı sonraki güncellemeleri bloklayabiliyor — issue #50483).
5. **Threading (doğrulandı):** `NowDockBand` property'leri düz timer thread'inden set ediyor, dispatcher gerekmiyor. `GetItems()` asla bloklamaz (COM çağrısı) — son snapshot'ı döner, gerekirse async tazeleme tetikler.
6. **Yaşam döngüsü:** Host provider'ı band pinlenmemiş olsa da oluşturur → monitor **lazy** başlar (ilk `GetDockBands()`/`TopLevelCommands()`), `Dispose()`'ta durur.
7. **Süreç örnekleme:** Tercih: tek `NtQuerySystemInformation(SystemProcessInformation)` P/Invoke → PID, ad, CreateTime, User/KernelTime, PrivatePageCount, WorkingSet; handle açmaz, elevated süreçlerde access-denied yok (elevated CPU hog'ları da top-5'te görünür). Spec'teki `Process.GetProcesses()` yaklaşımından sapma → `DECISIONS.md`. Kimlik anahtarı (PID, CreateTime) aynen korunur.
8. **İkon:** `QueryFullProcessImageName` → `new IconInfo(exePath)`; çıkarma işini host yapar (A1'de teyit). Erişilemezse generic glyph.
9. **Toast:** WinRT `ToastNotificationManager` (paket kimliğiyle kayıtsız çalışır), sabit tag ile tekrar yerine replace. Toast tıklamasıyla CmdPal'de Top-5 açma **host API'si yok → kapsam dışı** (spec "if feasible" diyor).
10. **Ayarlar:** Toolkit'te numeric setting yoksa `TextSetting` + parse/clamp (A1'de teyit).
11. **Lokalizasyon:** Built-in eklentiler `.resx` kullanıyorsa ona uyulur (spec `.resw` diyor).

## 2. Adımlar (her adım: build → deploy → CmdPal reload → doğrula → commit)

### Faz A — Araştırma & iskelet
- [x] A1. (Sonnet, read-only) PowerToys repo + yüklü NuGet: `NowDockBand`, Performance Monitor band, `WrappedDockItem`, `ICommandProvider3/4`, `JsonSettingsManager` + setting türleri, `CommandResult.Confirm`, `ToastStatusMessage`, `IconInfo(exePath)`, resx/resw → bulgular doğrudan `DECISIONS.md`'ye
- [x] A2. `git init` + `.gitignore` + boş `DECISIONS.md`
- [x] A3. **👤 Kullanıcı adımı:** CmdPal'de **"Create extension"** → `SysPulse` (bu klasöre). SDK sürümünü doğrula (≥ 0.9.260303001; muhtemelen zaten öyle)
- [x] A4. `SysPulse.Core` + `SysPulse.Tests` ekle, solution'a bağla
- [x] A5. Dev loop scripti `scripts/deploy.ps1`: çalışan SysPulse sürecini durdur → `dotnet build -p:Platform=x64` → `Add-AppxPackage -Register <out>\AppxManifest.xml` → CmdPal "Reload extensions". Extension'ın yüklendiğini doğrula → commit

### Faz B — Sistem örnekleme & canlı band
- [x] B1. `SystemSampler` (`GetSystemTimes` delta, `GlobalMemoryStatusEx`) + testler
- [x] B2. `StatusDockItem` + `WrappedDockItem` ile `CPU x% · MEM y%` canlı band (Id'ler non-empty); Dock'a pinle, gözle doğrula → commit

### Faz C — Opsiyonlar, durum makinesi & alarm görseli
- [x] C0. `SysPulseOptions` POCO (13 anahtar, clamp, `RetryInterval < ScanInterval`) + test 9  ← ayarları tüketen fazlardan önce
- [x] C1. `Models.cs` (`SystemSnapshot`, `ProcessSample`, `HealthState`, `BreachKind`)
- [x] C2. `HealthMonitor` (NORMAL/VERIFYING/ALERT, retry cadence, hysteresis, BreachKind, overlap guard) + test 1–5 + VERIFYING sırasında ayar değişimi testi
- [x] C3. ALERT görselleri: `warning-yellow.svg/png` (#FFC400), `⚠` başlık, subtitle; VERIFYING'de UI değişmez → görsel doğrulama → commit

### Faz D — Süreçler & Top-5
- [x] D1. `ProcessSampler` (NtQuerySystemInformation, (PID,CreateTime) cache, delta CPU, ilk gözlem = 0 %, prune) + test 6
- [x] D2. Ranking (Cpu / Memory / composite) + test 7
- [x] D3. `TopProcessesPage` + 5 slot, subtitle formatı, ikon, `Refresh`, non-blocking `GetItems()` + 2 sn tazelik kuralı, context menü (Open file location, Copy PID); unit test: `GetItems()` her çağrıda aynı referansları döner → commit

### Faz E — Kill
- [x] E1. `ProtectedProcessList` (tek yer; spec listesi + `Microsoft.CmdPal.UI`, `PowerToys`, `explorer`, `Environment.ProcessId`; case-insensitive, `.exe` opsiyonel; ayardan ek) + test 8
- [x] E2. `KillProcessCommand` (PID+CreateTime kimlik kontrolü, `ConfirmKill`, `KillProcessTree`, `Win32Exception(5)` → "yönetici yetkisiyle çalışan süreç sonlandırılamaz" mesajı, sonrası refresh — CPU% bir sonraki örnekte oturur) → elevated süreçle manuel test → commit

### Faz F — Ayarlar sayfası
- [x] F1. `SysPulseSettingsManager` (toolkit `JsonSettingsManager`, `Settings\SysPulseSettingsManager.cs`) → 13 anahtar (`TextSetting`/`ToggleSetting`) → `ToOptions()` ile `SysPulseOptions`'a map (parse: `SysPulse.Core.Settings.SettingParsers.ParseNumber`, invariant + current culture, sonra `.Normalize()`; clamp değiştirdiyse `TextSetting.Value`'ya geri yaz)
- [x] F2. Settings-changed → `_options` (volatile), `_monitorLoop.UpdateOptions`, `RebuildProtectedProcessList`, son `(evaluation, snapshot)`'tan `StatusDockItem.Apply` anında yeniden çağrılır (restart'sız); `CompactLabel`/`ShowToast`/`ConfirmKill`/`KillProcessTree` hepsi accessor üzerinden okunuyor. Görsel doğrulama (CmdPal'de ayar sayfasını açıp değiştirme) **insan tarafından yapılmalı** — bkz. görev raporu. Build 0 warning, `dotnet test` 123/123 yeşil; commit yapılmadı (görev talimatı).

### Faz G — Bildirim
- [x] G1. `AlertNotifier` (ToastNotificationManager, epizot başına 1 toast, `ShowToast`, hata → logla-devam) → commit

### Faz H — Cila & ölçüm
- [ ] H1. Lokalizasyon en-US + tr-TR
- [ ] H2. Rolling file log (LocalState, ~1 MB, debug kapalı)
- [ ] H3. `TESTING.md` (CPU/mem stres scriptleri, dikey Dock, label-off, elevated kill)
- [ ] H4. Overhead ölçümü (10 dk, <%1 CPU, <60 MB) + leak testi (30 dk @ 2 sn, private bytes düz) → `DECISIONS.md`
- [x] H5. ARM64 derleme kontrolü
- [ ] H6. Son review (bug-expert / Fable diff review) → commit

## 3. Riskler

| Risk | Etki | Önlem |
|---|---|---|
| Tam VS IDE yok; MSIX build CLI'dan | Faz A bloklanabilir | A5'te erken doğrula; eksik Build Tools bileşeni varsa kullanıcıya bildir |
| SDK API adları spec'ten farklı | Derleme hatası | A1, sapmalar `DECISIONS.md` |
| Host extension sürecini tutuyor | Re-deploy başarısız | `deploy.ps1` süreci durdurur |
| Toast kurumsal politika ile engelli olabilir | G fazı | Logla-devam; sarı band minimum |
| Dock label kesilmesi | UX | `CompactLabel`; açık soru 1 |
| Enterprise sideload politikası | Deploy | Dev Mode açık; A5'te erken test |

## 4. Açık sorular (spec §11)
1. Dock label kesilirse format tercihi?
2. Top-5'te çok-süreçli uygulamalar isme göre gruplansın mı? (v1 varsayılan: hayır)
3. Sistem toplamı eşik altındayken tek süreç sürekli yüksekse alarm olsun mu? (v1 varsayılan: hayır)

## 5. Çalışma modeli
Opus: spec/karar/onay · Fable: plan & diff critique · Sonnet: faz bazlı implementasyon (her faz ayrı sub-agent).

## Review
_(implementasyon sonrası doldurulacak)_
