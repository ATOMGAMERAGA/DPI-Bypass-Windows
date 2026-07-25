# Üçüncü taraf bileşenler

Atom DPI Bypass aşağıdaki bileşenleri değiştirmeden birlikte dağıtır.

## WinDivert

- Proje: <https://github.com/basil00/WinDivert>
- Yazar: Basil (basil00)
- Dosyalar: `WinDivert.dll`, `WinDivert64.sys`
- Lisans: LGPL v3 (ikili dağıtım için) / GPL v2 seçeneği

WinDivert, Windows'ta ağ paketlerini kullanıcı kipinde yakalayıp yeniden
göndermeyi sağlayan bir kullanıcı kipi kütüphanesi ve çekirdek sürücüsüdür.
Atom DPI Bypass, DPI aşma yöntemlerini uygulamak için bu kütüphaneyi kullanır.

İkili dosyalar üzerinde hiçbir değişiklik yapılmamıştır ve yayıncı imzaları
korunmuştur; kurulum paketine alınmadan önce Authenticode imzaları derleme
hattında doğrulanır. Lisans metni, kurulum klasöründeki
`WinDivert-LICENSE.txt` dosyasında yer alır.

LGPL v3 uyarınca, kütüphanenin bu uygulamayla birlikte kullanılan sürümü
yukarıdaki bağlantıdan kaynak koduyla birlikte edinilebilir ve kullanıcı,
kütüphaneyi kendi derlediği uyumlu bir sürümle değiştirebilir: kurulum
klasöründeki `WinDivert.dll` ve `WinDivert64.sys` dosyalarını aynı ada sahip
uyumlu dosyalarla değiştirmek yeterlidir.

## .NET çalışma zamanı

- Proje: <https://github.com/dotnet/runtime>
- Lisans: MIT

Uygulama, .NET çalışma zamanını kendi içinde barındıran (self-contained)
biçimde yayınlanır; bu nedenle çalışma zamanı dosyaları kurulum klasöründe yer
alır.

## Inno Setup

- Proje: <https://jrsoftware.org/isinfo.php>
- Lisans: Inno Setup License

Yalnızca kurulum paketini üretmek için derleme sırasında kullanılır; uygulamayla
birlikte dağıtılmaz.
