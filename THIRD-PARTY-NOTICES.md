# Üçüncü taraf bileşenler

DPI Bypass aşağıdaki bileşenleri değiştirmeden birlikte dağıtır.

## WinDivert

- Proje: <https://github.com/basil00/WinDivert>
- Yazar: Basil (basil00)
- Dosyalar: `WinDivert.dll`, `WinDivert64.sys`
- Lisans: LGPL v3 (ikili dağıtım için) / GPL v2 seçeneği

WinDivert, Windows'ta ağ paketlerini kullanıcı kipinde yakalayıp yeniden
göndermeyi sağlayan bir kullanıcı kipi kütüphanesi ve çekirdek sürücüsüdür.
DPI Bypass, DPI aşma yöntemlerini uygulamak için bu kütüphaneyi kullanır.

İkili dosyalar üzerinde hiçbir değişiklik yapılmamıştır ve yayıncı imzaları
korunmuştur; kurulum paketine alınmadan önce Authenticode imzaları derleme
hattında doğrulanır. Lisans metni, kurulum klasöründeki
`WinDivert-LICENSE.txt` dosyasında yer alır.

LGPL v3 uyarınca, kütüphanenin bu uygulamayla birlikte kullanılan sürümü
yukarıdaki bağlantıdan kaynak koduyla birlikte edinilebilir ve kullanıcı,
kütüphaneyi kendi derlediği uyumlu bir sürümle değiştirebilir: kurulum
klasöründeki `WinDivert.dll` ve `WinDivert64.sys` dosyalarını aynı ada sahip
uyumlu dosyalarla değiştirmek yeterlidir.

## Microsoft Fluent System Icons

- Proje: <https://github.com/microsoft/fluentui-system-icons>
- Lisans: MIT

Arayüzdeki simgeler bu aileden alınmıştır. `src/DpiBypass.App/Theme/Icons.xaml`
dosyasındaki her geometri, resmî SVG varlıklarının yol verisidir; elle yeniden
çizilmemiş, yalnızca WPF'nin yol dilbilgisine aktarılmıştır (dolgu kuralı `F1`,
SVG'nin varsayılan `nonzero` kuralına karşılık gelir). Dosya
`tools/fetch-fluent-icons.py` ile üretilir; aynı betik `--check` seçeneğiyle
çalıştırıldığında depodaki geometrilerin hâlâ üst kaynakla aynı olduğunu
doğrular.

Her simge, çizildiği boyutta indirilir: 16-20 DIP için 20 piksellik çizim,
24 DIP ve üzeri için 24 piksellik çizim kullanılır. Ölçeklenmiş tek bir çizim
kullanılmaz.

MIT lisans metni yukarıdaki bağlantıdaki `LICENSE` dosyasında yer alır.

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

## Tesseract OCR ve İngilizce dil modeli

- Proje: <https://github.com/tesseract-ocr/tesseract>
- .NET bağlayıcısı: <https://github.com/charlesw/tesseract>
- Dil verisi: <https://github.com/tesseract-ocr/tessdata_fast>
- Lisans: Apache License 2.0

Yalnız kullanıcının seçtiği VALORANT Network RTT sayı alanını bellekte okumak
için kullanılır. Yakalanan görüntüler dosyaya yazılmaz.
