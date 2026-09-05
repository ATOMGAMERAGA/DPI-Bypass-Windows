# Başlangıç performansı

Açılışın iki ayrı bekleme noktası vardır: ilk pencerenin çizilmesi ve koruma
motorunun `Running` durumuna geçmesi.

- Kapsam, alan adları, bağlantı ve DNS/ayarlar sayfaları ilk ziyaretlerinde
  oluşturulur. Oluşan kontrol ağacı sekmede saklanır; tekrar ziyaretler arama
  metnini, kaydırma konumunu ve açık ayrıntıları yeniden oluşturmaz. Ana sayfa ve
  günlük başlangıçta hazırdır. Motor ve ayarlar sayfaların açılmasına bağlı değildir.
- DNS sunucuları ve arayüz indeksleri önce .NET ağ API'sinden okunur. Başarılı
  okumada DNS envanteri için PowerShell başlatılmaz. Okunamayan bağdaştırıcılar
  mevcut toplu PowerShell sorgusuna gider. DNS yazma, yedekleme, geri yükleme ve
  çökme kurtarma sırası korunur.

## Doğrulama

`DnsEnumerationTests` süreç başlatmadan okumayı, adreslerin sırasını ve ailelerini,
yerel proxy adreslerinin yedekten çıkarılmasını, IPv6 kapalı bağlantıyı, etkin
bağdaştırıcı bulunmamasını, yedek sorguyu ve iptali sınar. Mevcut DNS kurtarma ve
başlangıç testleri de çalıştırılmalıdır.

Windows derlemesindeki `DpiBypass.exe --ui-selftest`, dört ertelenen sayfanın ilk
kare öncesinde oluşturulmadığını; bütün sekmelerin açıldığını, doğru veri modeline
bağlandığını ve tekrar ziyaretlerde aynı kontrolleri koruduğunu doğrular. Ayarlar
sayfasının bölüm kısayolları ve ilerleme alanı da iki tema ve iki pencere boyutunda
sınanır. Bu kip ağ motorunu açmaz.

Gerçek süre karşılaştırması için aynı Windows bilgisayarda önceki ve yeni sürümü,
aynı ayarlar ve ağla birkaç kez başlatın. İlk kareyi ve korumanın etkinleşmesini
ayrı ölçün; ilk soğuk açılışı sonraki açılışlardan ayrı değerlendirin. Wi-Fi ve
Ethernet, IPv6 açık/kapalı, tepsiden açma, DNS kipleri ve normal kapatmada DNS geri
yükleme senaryolarını kontrol edin. Linux'taki derleme ve birim testleri Windows
sürücüsünün veya WPF penceresinin gerçek çalışma süresini ölçmez.

## Bu değişiklikte alınan sonuçlar

- Release WPF derlemesi: başarılı, 0 uyarı / 0 hata.
- Birim testleri: 950 testin 949'u başarılı; altı yeni testin tamamı başarılı.
- `ATcpProbeNeverOutlastsItsOwnDeadline`, bu ortamda `192.0.2.1:443`
  bağlantısı başarılı olduğu için başarısız. Değişiklik öncesi `HEAD` kodunun
  ayrı geçici kopyasında aynı test aynı nedenle başarısız oldu.
- XAML kaynak kontrolü: 450 başvuru, eksik veya ileri başvuru yok; tema
  paletlerinin anahtarları eşleşiyor. PowerShell bulunmadığı için kaynak
  denetiminin eşdeğeri Python ile çalıştırıldı.
- Gerçek WPF çalışma sınaması ve sürücü/DNS entegrasyonu Linux ortamında
  çalıştırılmadı; Windows CI / Windows bilgisayar doğrulaması gerektirir.
