# Arka plandaki kaynak kullanımı

Uygulama tepside ya da simge durumundayken korumayı sürdürür; buna karşılık
görüntülemeye ait hiçbir işi yapmaması gerekir. Bu belge, "arka plan" durumunda
neyin çalıştığını ve neyin durduğunu yazar.

## Pencere görünmezken duran işler

- **Sayaç zamanlayıcısı durur.** Paket toplamları, DNS özeti ve "3 dk önce" gibi
  göreli zaman metinleri yalnızca pencere ekrandayken yeniden biçimlendirilir.
  Pencere geri geldiğinde hepsi anında yeniden okunur, yani ekrandaki değer bir
  aralık eski olmaz.
- **Simge durumu da "görünmüyor" sayılır.** WPF'te simge durumuna küçültülmüş bir
  pencerede `IsVisible` hâlâ `true` olduğu için, eskiden yalnızca tepsiye alma
  algılanıyordu; görev çubuğuna küçültülen pencere iki saniyede bir sayaç
  biçimlendirmeye devam ediyordu.
- **Tepsiye açılan başlangıç.** `StartMinimised` ile açılan bir oturumda pencere
  hiç gösterilmediği için görünürlük olayı da hiç gelmiyordu. Görüntüleme artık
  kapalı başlar ve pencere gerçekten gösterildiğinde açılır.
- **Günlük ve "korunan site" kuyrukları bekletilir.** Satırlar kendi sınırlı
  kuyruklarında birikir; her satır için ayrı bir dispatcher işi, koleksiyon
  değişikliği ve WPF kapsayıcı geçişi kurulmaz. Pencere döndüğünde toplu olarak
  aktarılır. Sayfa zaten yalnızca son birkaç yüz satırı gösterir ve dosyaya her
  şey yazılmaya devam eder.
- **Korunan site kuyruğu sınırlıdır.** Bu kuyruk paket yolundan, yeniden yazılan
  her el sıkışma için beslenir. Sayfa en yeni 100 adı gösterdiğinden kuyruk 400
  ile sınırlandı; en eskiler düşer.

Koruma motoru, ağ izleyicisi, DNS proxy'si, düzenli denetim ve kullanıcının
açtığı hiçbir özellik bu durumdan etkilenmez.

## Belleğin geri verilmesi

Pencere beş saniye görünmez kaldıktan sonra:

1. Toplayıcıya bir geçişin işe yarayıp yaramayacağı **sorulur**
   (`GCCollectionMode.Optimized`, engellemeyen). Zorlanmaz.
2. `SetProcessWorkingSetSize(-1, -1)` ile çalışma kümesi kırpılır.

İkisi de ipucudur: hiçbir şey atılmaz, hâlâ kullanılan sayfalar bir sonraki
dokunuşta geri gelir. Pencereyi geri getirmek, henüz gerçekleşmemiş kırpmayı
iptal eder; böylece küçült-geri getir hareketi kırpma ve hemen ardından sayfa
hatası anlamına gelmez.

Ayrıca:

- **Ekran okuma (OCR) motoru her ölçümün sonunda bırakılır.** Tesseract'ın
  İngilizce LSTM modeli 4 MB'lık bir dosyadır ve yüklendiğinde onlarca MB'lık
  yerel ayırma olur. Eskiden ilk ölçümden sonra süreç kapanana kadar yüklü
  kalıyordu. Sonraki ölçümde tembel olarak yeniden kurulur; maliyeti o ölçümün
  ilk birkaç yüz milisaniyesidir.

## İki ağ izleyicisi yerine bir tane

"Hangi ağdayız" sorusunun cevabı ucuz değildir: her bağdaştırıcı, adresleri, bayt
sayaçları, kablosuz ilişkilendirme ve ağ geçidinin ARP karşılığı okunur. Bu soru
on saniyede bir sorulur.

Koruma servisi süreç boyunca bir izleyici tutar (Vodafone kartının, koruma kapalı
olsa bile ağ adını bilmesi gerekir). Düşük gecikme kipi açıldığında ise ikinci bir
izleyici daha kuruluyordu ve aynı cevaba varmak için aynı iş iki kez yapılıyordu.
Artık gecikme şeridi, servisin izleyicisinin **sahiplenmeyen bir görünümünü**
alır: `Start` bir şey yapmaz, `Dispose` yalnızca kendi aboneliklerini bırakır.
Kipin kapatılması uygulamanın geri kalanından ağ adını almaz. Ömürlük izleyici
hiç başlatılamazsa şerit yine kendi izleyicisini kurar, yani dolaşım algısı
kaybolmaz.

Tek izleyiciyi paylaşmanın bir sonucu daha var: aboneler artık birbirinin sırasını
bekliyor. Bu yüzden `NetworkMonitor` her aboneyi ayrı ayrı çağırır; birinin hata
vermesi diğerinin bildirimi almasını engellemez.

## Çalışma zamanı ayarları

`DpiBypass.App.csproj` içinde, SDK varsayılanlarına bırakmak yerine yazıya
döküldü:

- `ServerGarbageCollection=false` — sunucu GC'si çekirdek başına bir yığın ve bir
  iş parçacığı ayırır. Canlı kümesi birkaç on MB olan bir masaüstü uygulaması için
  bu, modern bir makinede yüzlerce MB'lık ayırma demektir. Burada asla açılmamalı.
- `ConcurrentGarbageCollection=true` — bilerek açık. Engelleyen bir gen2 duraklaması
  paket yoluna ve gecikme şeridine doğrudan yansır; bunun bedeli, karşılığında
  gelen daha büyük gen0 bütçesinden değerlidir.
- `RetainVMGarbageCollection=false` — boşalan bölütler beklemede tutulmaz, işletim
  sistemine geri verilir.

## Doğrulama

`BackgroundFootprintTests` paylaşılan ağ görünümünü (iletme, ikinci bir yoklama
başlatmama, sahibi kapatmama), OCR motorunun güvenle bırakılabilmesini, kuyruk
sınırlarını, görüntüleme kapısını ve yayımlanan GC yapılandırmasını sınar. Mevcut
başlangıç, pencere görünürlüğü ve gecikme testleri de çalıştırılmalıdır.

Gerçek ölçüm için aynı Windows bilgisayarda önceki ve yeni sürümü açın, pencereyi
tepsiye alın ve beş saniye bekleyin; Görev Yöneticisi'nde çalışma kümesini ve
uygulamanın CPU payını karşılaştırın. Düşük gecikme kipi açık ve kapalıyken ayrı
bakın: iki izleyicinin tekleşmesi yalnızca kip açıkken görülür. Linux'taki derleme
ve birim testleri Windows sürücüsünün veya WPF penceresinin gerçek çalışma süresini
ölçmez.
