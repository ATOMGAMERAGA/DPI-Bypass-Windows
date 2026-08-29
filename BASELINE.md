# Baseline Kaydi

Denetim tarihi: 2026-08-28 (America/Los_Angeles)

## Kaynak ve Butunluk

- Kullanici talebinde belirtilen `DPI-Bypass-Windows-main-1.zip` calisma alaninda bulunamadi. Bu nedenle beklenen `1f6a...` SHA-256 degeri dogrulanamadi.
- Talep metni eki icin hesaplanan SHA-256 `16E8272AC681A8EA41ACFEFE430A2C2FD677C28344F82C9ECEFCE92DC5352F94` degeridir. Bu deger ZIP arsivine ait degildir.
- Teslim edilen calisma agacinda baslangicta `.git` metaverisi yoktu. Kullanici commit/push istedikten sonra depo `origin/main` (`42c3616befaa4d64c245f9161eec3ba3ceb9e7ee`) temel alinarak Git deposuna baglandi.
- Rapor dosyalari eklenmeden once, `.git` haric calisma agaci envanteri 125 dosya ve 4.754.301 baytti.

## Teknik Harita

- Uygulama: .NET 10, WPF masaustu arayuzu ve cekirdek kutuphane.
- Ag katmani: WinDivert tabanli paket yakalama/yeniden yazma, DNS vekili ve DoH istemcisi.
- Kontrol kanali: Windows named pipe IPC.
- Paketleme: PowerShell kurulum betikleri ve Inno Setup.
- Testler: xUnit C# testleri ile PowerShell kurulum/XAML kaynak testleri.

## Ortam

- Isletim sistemi API gorunumu: `Microsoft Windows NT 10.0.28000.0`, 64 bit.
- Kayit defteri gorunumu: Windows 10 Pro, DisplayVersion 26H1, build 28000.1.
- `dotnet` host/runtime: 8.0.14 x64.
- Yerel .NET SDK: yok (`dotnet --info` hic SDK listelemedi).

## Baslangic Dogrulamalari

| Islem | Sonuc | Aciklama |
|---|---|---|
| Kaynak ZIP SHA-256 | NOT RUN | Arsiv calisma alaninda yoktu. |
| Git durum/tarihce | NOT RUN | Baslangicta `.git` yoktu. |
| `dotnet restore/build/test` | NOT RUN | Yerel makinede .NET SDK yoktu. |
| `scripts/tests/install.tests.ps1` | PASS | Tum kurulum betigi testleri gecti. |
| `scripts/tests/xaml-resources.tests.ps1` | PASS | 49 kaynak anahtari, 203 referans ve iki 20 anahtarli palet dogrulandi. |
| Windows hizmet/WinDivert gercek trafik testi | NOT RUN | Yonetici yetkili, sistem durumunu degistiren VM testi yapilmadi. |
| Statik analiz/format/lisans taramasi | NOT RUN | Yerel SDK ve ayrilmis arac zinciri yoktu. |

## Degisiklik Sonrasi Kanit

Bu bolum baseline sonucu degildir; onarimlar sonrasindaki CI kanitidir.

- GitHub Actions run #41: basarili, 232/232 C# testi ve tum PowerShell testleri gecti.
- GitHub Actions run #42: basarili, 242/242 C# testi ve tum PowerShell testleri gecti.
- Run #42 ayrica Release publish, publish cikti dogrulamasi, Inno Setup derlemesi ve artifact yuklemesini tamamladi.
- Release yayim adimi, dal `main` olmadigi icin tasarlandigi sekilde atlandi.
