# Kurulabilir sürüm (imzalı MSIX + GitHub Release) ve uygulama ikonu

**Durum:** BLOKE
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
- [ ] 5. Paketi yerelde kurup bildirim ikonunu ve çalışmayı doğrula.
- [ ] 6. Commit + tag `v1.0.0` + GitHub Release (msix'ler, `.cer`, kurulum talimatı).

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

## B. RAPOR (iş bittikten sonra)
