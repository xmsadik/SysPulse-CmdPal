# Kurulabilir sürüm (imzalı MSIX + GitHub Release) ve uygulama ikonu

**Durum:** TAMAMLANDI
**Tarih:** 2026-10-09 (son güncelleme: 2026-10-09)
**Etkilenen yer:** `SysPulse/SysPulse/Assets`, `Package.appxmanifest`, `SysPulse.csproj`, yeni `scripts/package.ps1`, GitHub release. SAP'a yazma yok.

## A. BRIEF (işe başlamadan önce)
### Özet
SysPulse'un başka bilgisayarlara kurulabilmesi için kendi oluşturduğumuz bir sertifikayla imzalanmış MSIX paketleri (x64 ve ARM64) üretilecek ve GitHub Release olarak yayınlanacak. Bunun yanında Windows bildirimlerinde görünen çarpı şeklindeki ikon, uygulamanın nabız (pulse) logosuyla değiştirilecek.

### Arka plan
- **MSIX:** Windows'un modern uygulama paketi biçimi. Windows yalnızca imzalı MSIX'i kurar ve imzalayan sertifikanın konusu (subject) manifest'teki `Publisher` alanıyla birebir aynı olmalıdır.
- **Kendinden imzalı sertifika:** Bir sertifika otoritesi tarafından verilmediği için kuran kişinin `.cer` dosyasını bir kez "Güvenilen Kişiler" (Trusted People) deposuna eklemesi gerekir.
- **Çarpı ikon:** `Assets` klasöründeki PNG'ler proje şablonunun yer tutucu görselleri (gri kare içinde çarpı). Windows bildirimleri uygulama ikonu olarak `Square44x44Logo`'yu kullandığı için bildirimlerde bu çarpı görünüyor.

### Kapsam / Kapsam dışı
- Kapsam: ikon setinin yeniden üretilmesi, manifest kimliği ve sürümü, imzalama, x64 + ARM64 paketleri, GitHub Release, kurulum talimatı.
- Kapsam dışı: Microsoft Store ve winget (kullanıcı 1. seçeneği seçti).

### Plan
- [x] 1. `pulse.svg` tabanlı ikon setini (tüm ölçekler ve targetsize varyantları) üret, şablon PNG'lerini değiştir.
- [x] 2. Manifest: Publisher `CN=Mustafa Sadik`, PublisherDisplayName "Mustafa Sadik", sürüm `1.0.0.0`, açıklama metni.
- [x] 3. Kendinden imzalı kod imzalama sertifikası oluştur (özel anahtar repoya girmez).
- [x] 4. `scripts/package.ps1`: Release derleme, x64 + ARM64 MSIX üretimi ve imzalama.
- [x] 5. Paketi yerelde kurup bildirim ikonunu ve çalışmayı doğrula.
- [x] 6. Commit + tag `v1.0.0` + GitHub Release (msix'ler, `.cer`, kurulum talimatı).

### Riskler ve varsayımlar
- Publisher adı olarak git kullanıcı adı (`Mustafa Sadik`) varsayıldı; ileride değişirse paket kimliği değişir ve eski kurulum yeni sürüme güncellenemez (kaldırıp kurmak gerekir).
- Publisher değişince paket aile adı (`SysPulse_8wekyb3d8bbwe`) da değişir; geliştirme kurulumunun ayarları yeni pakete taşınmaz.
- Sertifikanın `.pfx` dosyası repoya konmaz; kaybolursa sonraki sürümler yeni sertifikayla imzalanır ve kullanıcılar yeni `.cer`'i tekrar eklemek zorunda kalır.

### ⚠ Senden beklenenler (başlamadan önce)
| # | Karar / eylem | Neden gerekli | İşi durduruyor mu? (Evet/Hayır) | Cevap |
|---|---|---|---|---|
| 1 | Publisher adı `Mustafa Sadik` olarak varsayıldı; farklı olsun istersen söyle | İmza ve paket kimliği buna bağlı | Hayır | — |

### Kararlar
- 2026-10-09: Dağıtım yolu soruldu (kendinden imzalı + GitHub Release / Store / winget). Kullanıcı 1. seçeneği seçti ve bildirimlerdeki çarpı ikonun düzeltilmesini istedi.

- 2026-10-09: Sertifika `CN=Mustafa Sadik` (parmak izi DAA0349C…6796, 2031'e kadar geçerli) CurrentUser\My deposunda oluşturuldu. Paketler `dist/` altında üretildi, 162 birim testi geçti. Kurulum doğrulaması için sertifikanın LocalMachine\TrustedPeople deposuna eklenmesi gerekiyor. Bu işlem yönetici yetkisi istiyor ve oturum yönetici değil. Kullanıcıya soruldu.

- 2026-10-09: Kullanıcı sertifikayı LocalMachine\TrustedPeople deposuna ekledi. Paket kuruldu, kullanıcı bildirim ikonunu ve Dock'taki çalışmayı onayladı. Kullanıcı GitHub'da ücretsiz yayın istedi ve yayınlandı.

## B. RAPOR (iş bittikten sonra)
### Sonuç
SysPulse 1.0.0 imzalı MSIX paketleri olarak GitHub Release'te ücretsiz yayında: https://github.com/xmsadik/SysPulse-CmdPal/releases/tag/v1.0.0. Bildirimlerdeki çarpı ikonu nabız logosuyla değiştirildi.

### Yapılanlar
- Şablon yer tutucu PNG'ler, `docs/app-logo.svg`'den üretilen 63 ikon dosyasıyla değiştirildi (tüm scale ve targetsize varyantları).
- Manifest: Publisher `CN=Mustafa Sadik`, sürüm `1.0.0.0`, açıklama metni. csproj artık `Assets\**` klasörünün tamamını içeriyor.
- `scripts/package.ps1`: x64 + ARM64 imzalı MSIX ve `SysPulse.cer` dosyasını `dist/` klasörüne üretiyor.
- Kurulum talimatlı `README.md` eklendi, `dist/` git'te yok sayılıyor.
- Commit `cb529ce`, tag `v1.0.0`, GitHub Release (2 msix + cer).

### Nasıl doğrulandı
- 162 birim testinin hepsi geçti.
- İki paketin imzası `CN=Mustafa Sadik`. x64 paketi `Add-AppxPackage` ile kuruldu, durum Ok.
- Kullanıcı deneme bildiriminde yeni ikonu ve Dock'ta çalışmayı gözle onayladı.
- ARM64 paketi kurularak test edilmedi, çünkü ARM cihaz yok. Yalnızca derlendi ve imzalandı.

### Değişen nesneler / dosyalar
`SysPulse/SysPulse/Assets/*`, `Package.appxmanifest`, `SysPulse.csproj`, `scripts/package.ps1`, `docs/app-logo.svg`, `README.md`, `.gitignore`.

### Yapılmayanlar ve açık kalanlar
- Store ve winget: kapsam dışı (kullanıcının seçimi).
- Kendinden imzalı sertifika nedeniyle kullanıcıların `.cer` dosyasını bir kez yönetici olarak eklemesi gerekiyor. Bu, ücretsiz yolun bilinen bir sınırı.

### ⚠ Senden beklenenler (şimdi)
| # | Eylem | Neden | Aciliyet | İşi durduruyor mu? (Evet/Hayır) |
|---|---|---|---|---|
| 1 | Sertifikayı parolalı bir `.pfx` olarak yedekle (Cert:\CurrentUser\My, parmak izi DAA0349C…6796) | Makine kaybolursa sonraki sürümler aynı imzayla güncellenemez | Orta | Hayır |

### Sonraki oturum için not
Yeni sürüm için manifest `Version` artırılır, `scripts/package.ps1` çalıştırılır, `gh release create vX.Y.Z dist/*` ile yayınlanır. Kurulu paket ailesi artık `SysPulse_azw75jah4f396`, ayarlar ve loglar `%LOCALAPPDATA%\Packages\SysPulse_azw75jah4f396\LocalState` altında.
