# Uygulama kalite geçişi — kararlılık, hız, kullanılabilirlik

Başlangıç noktası: `main` üzerindeki `0b9efd0c7203b20dfa7dce96b8e4b56e6ffbac5c`.
Dal: `claude/new-session-5gljex`.

Bu belge yapılan mühendislik işinin kaydıdır; işin yerine geçmez. Her başlık altında
**ne değişti**, **hangi kanıtla** ve **neyin ölçülmediği** ayrı ayrı yazılıdır.

---

## 1. Başlangıç durumu

| Kapı | Sonuç |
| --- | --- |
| `dotnet test` (referans commit) | 799 başarılı, 0 başarısız |
| `dotnet build DpiBypass.slnx -c Release` | başarılı |
| `scripts/tests/*.tests.ps1` | üçü de başarılı |

Önceden var olan başarısız test yoktu. Aşağıdaki her başarısızlık bu geçiş sırasında
tanıtılıp aynı geçişte düzeltilmiştir.

---

## 2. P0 — Ağ işlerinin birbirine karışması *(doğrulandı, düzeltildi)*

**Doğrulama.** Referans kodda dört yol motorun paylaşılan `Strategy` alanına yazıyordu:
ilk başlatma, ağ değişimi, düzenli denetim ve elle yeniden ayarlama. `StrategyTuner`
her adayı motora kurup ölçüyor, `finally` bloğunda da `_engine.Strategy = winner ??
previous` ile geri yazıyordu. `RecordNetworkResult` ise yazma anında servisin *güncel*
`Network.Key` değerini okuyordu. Sonuç: A ağında başlayan bir tarama, makine B ağına
geçtikten sonra hem motoru değiştirebiliyor hem de A'da ölçülen sonucu B'nin profiline
yazabiliyordu.

**Değişiklik.** `Diagnostics/StrategyCoordinator.cs`: her iş bir `StrategyLease` alır;
lease ağ anahtarını, motor oturumunu ve nesil numarasını taşır. Devredışı kalmış bir
lease hiçbir şey yazmaz — `finally` bloğundaki geri yazma dahil. `StrategyTuner` artık
motoru değil `IStrategyWriter`'ı alır, dolayısıyla kural çağıranların hatırlamasına değil
tipe bağlıdır. Otomatik istekler aynı ağ için süren işe katılır; elle istek süren işi
devralır; `ApplyImmediate` kullanıcının elle seçtiği yöntemi tarama sırasında bile
kalıcı kılar.

**Bilinçli ödünleşme.** Devredışı bırakılan bir iş kısa bir süre (5 sn) beklenir; kendi
iptalini yok sayarsa yeni iş onu beklemeden başlar. Süresiz beklemek, çekirdek çağrısında
takılmış bir ölçümün oturumun kalanındaki her yeniden ayarlamayı bloke etmesi demekti —
yani serileştirmenin önlemek için var olduğu kilitlenmenin ta kendisi. Yazma hakkı
turnikeyi tutmaya değil, güncel iş olmaya bağlı; bu yüzden bu güvenli.

**Kanıt.** `StrategyCoordinationTests` (11 test) ve `StrategySweepTests` (5 test), hepsi
elle serbest bırakılan `TaskCompletionSource`'larla; hiçbiri `Sleep` süresine bağlı değil.
Kapsanan senaryolar: A'nın yavaş işi B'den sonra biterse hiçbir şey yazamaz; devredışı
lease hâlâ hangi ağı ölçtüğünü bilir; elle istek süren otomatik taramayı devralır ve
onun `finally` geri yazması reddedilir; 8 eşzamanlı otomatik istek tek iş çalıştırır;
64 karışık istek kilitlenmez; durdurulmuş oturumun işi yeni motora yazamaz.

**Yaşam döngüsü.** `StartAsync` gövdesi artık çağıranın token'ı ile servis ömrünü
birleştiren bir token üzerinde çalışır: başlatmayı iptal etmek yarım DNS ayarı
bırakmaz, çalışan servis de alakasız bir çağrı token'ına bağlanmaz. Arka plan işleri
`TrackedWork` ile izlenir ve kullandıkları nesneler kapatılmadan önce sınırlı bir bütçe
(5 sn) içinde boşaltılır. `TeardownAsync` artık DNS geri yükleme hatasında fırlatmaz;
hatayı taşır, yerel DNS sunucusunu bilinçli olarak açık bırakır (bağdaştırıcılar hâlâ
127.0.0.1'e bakıyor) ve `StopAsync` bunu "tamamlandı" diye değil `DnsRestorePending` ve
açık bir hata olarak bildirir.

---

## 3. P0/P1 — DNS

### 3.1 TCP yanıtının tamamı *(doğrulandı, düzeltildi)*

Referans `HandleTcpClientAsync`, uzunluk öneki ve yanıtı tek `Socket.SendAsync`
çağrısına veriyor, dönen bayt sayısını kullanmıyordu. .NET belgesi bu değerin gönderilen
bayt sayısı olduğunu açıkça söyler; kısmi gönderimde istemci, kendi önekinin vaat
ettiğinden kısa bir mesaj alır ve gerisini zaman aşımına kadar bekler.

`DnsStreamTransport.SendAllAsync` üç çıkışlı bir döngüdür: tamamı gönderildi, karşı taraf
ilerleme bildirmiyor (sıfır), ya da iptal. Sıfır durumu önemli: akış soketinde sıfır
"karşı taraf gitti" demektir ve üzerinde dönmek, bir daha gelmeyecek istemci için istek
yuvası tutan meşgul bekleme olurdu.

RFC 7766 §6.2.1 uyarınca tek bağlantıda ardışık sorgular yanıtlanır; sorgu başına süre
sınırı (5 sn), boşta kalma sınırı (10 sn) ve bağlantı başına sorgu tavanı (64) ile.

### 3.2 UDP kesme *(doğrulandı, düzeltildi)*

Referans kod, istemcinin alabileceğinden büyük yanıtı olduğu gibi gönderiyordu.
`DnsMessage.GetClientUdpPayloadSize` EDNS0 OPT kaydından istemcinin tamponunu okur
(RFC 6891 §6.1.2), yoksa 512 kabul eder; büyük yanıt artık TC biti kurulmuş, yalnızca
başlık ve soru bölümünden oluşan doğru bir kesik yanıtla döner ve istemci TCP'ye geçer.

### 3.3 DoH paylaşımı ve sağlayıcı sağlığı *(doğrulandı, düzeltildi)*

Mevcut sağlayıcı zinciri, süre sınırları ve önbellek korundu; sıfırdan yazılmadı.
Eklenen ve düzeltilen:

- **Paylaşım.** Aynı anlamsal soru tek upstream isteğe biner. Anahtar, yalnızca işlem
  kimliği sıfırlanmış tam sorgudur; bayraklar, kayıt türü/sınıfı ve EDNS/DNSSEC farkları
  korunur. Her bekleyen kendi kopyasını kendi işlem kimliğiyle alır.
- **İptal.** Bir istemcinin vazgeçmesi diğerlerinin yanıtını iptal etmez; son bekleyen
  ayrılınca ortak iş bırakılır.
- **Yanlış yanıt cezalandırılır.** Sorguyla eşleşmeyen yanıt eskiden hiçbir kayıt
  bırakmadan atlanıyordu; sürekli yanlış yanıtlayan uç zincirin başında kalıyor ve her
  sorguda ilk o deneniyordu.
- **Ceza sona erer.** Ceza penceresi 60 sn'dir. Eskiden süresizdi: tek bir kesinti,
  kullanıcının tercih ettiği çözümleyiciyi oturum boyunca en sona atıyordu.
- **`ActiveProvider` ile sağlıklı sağlayıcı ayrıldı.** `ActiveProvider` "en son kim
  yanıtladı", `VerifiedProvider` "doğrulanmış ve cezalı olmayan kim" sorusunu yanıtlar.
- **Ağ geçişi.** `OnNetworkChanged` uç sağlığını sıfırlar ve bir epok ilerletir; geçişten
  önce başlamış bir sorgunun yanıtı, kendisini isteyen istemciye verilir ama yeni ağın
  önbelleğine yazılmaz.

**Kanıt.** `DnsStreamTransportTests` (5), `DnsUdpSizeTests` (7), `DnsProxyWireTests` (5,
gerçek loopback soketleri üzerinden), `DohSharingTests` (6), `DohEndpointHealthTests` (5).

---

## 4. P1 — Günlük ve arayüz yükü *(doğrulandı, düzeltildi)*

`AppLog.AppendToFile` her kayıt için kilit altında senkron `File.AppendAllText`
yapıyordu: paket iş parçacığı bir diske yazma bedeli ödüyor, kayıt yapmak isteyen diğer
her iş parçacığı arkasında kuyruğa giriyordu.

`Logging/LogFileWriter.cs`: üreticiler kuyruğa koyup döner, tek arka plan görevi her
uyanışın ardındaki yığını tek `append` ile yazar. Kuyruk sınırlıdır (50.000), taşma sayısı
dürüstçe raporlanır, günlük dosyası boyut sınırını aşınca numaralı parçalara devam eder —
14 günlük saklama ile birlikte bu, dizini de sınırlar. Kapanışta boşaltılır; süreç zorla
sonlandırılırsa son anların kaybedileceği kodda ve bu belgede açıkça yazılıdır.

Arayüz tarafında: bekleyen günlük kuyruğu sınırlı, tek Dispatcher geçişinde adet bütçesi
(200) ile boşaltılır, kalanı bir sonraki geçişe kalır; host olayları da aynı biçimde
toplu işlenir (eskiden el sıkışma başına bir Dispatcher işi); günlük sayfasında düzey
süzgeci ve arama vardır ve **kopyala görünen satırları kopyalar**, uyguladığı süzgeci de
günlüğe yazar. Pencere tepsideyken yalnızca sunum sayaçları seyrekleşir (2 sn → 20 sn);
koruma, ağ izleme ve DNS proxy'si etkilenmez, geri açılınca hemen tazelenir.

### Ölçüm — aynı makine, aynı disk

20.000 kayıt, 8 üretici iş parçacığı. "Eski", bu değişiklikten önceki `AppendToFile`
uygulamasının birebir kopyasıdır ve aynı süreçte çalıştırılmıştır.

| Tur | Eski toplam | Yeni toplam | Eski satır | Yeni satır |
| --- | --- | --- | --- | --- |
| 1 | 421 ms | 36 ms | 20000 | 20000 |
| 2 | 442 ms | 49 ms | 20000 | 20000 |
| 3 | 262 ms | 20 ms | 20000 | 20000 |

Tek bir `Write` çağrısının en kötü süresi de ölçüldü (eski 5–34 ms, yeni 4–18 ms) ancak
bu sayı paylaşımlı kapsayıcıda GC ve zamanlayıcı gürültüsüne açık; güvenilir bulmadığım
için üzerine iddia kurmuyorum.

**Bu ölçüm sırasında kendi tanıttığım bir hatayı buldum ve düzelttim:** yazıcı ilk
boşaltmadan önce tüm `FlushInterval` süresini bekliyordu, bu yüzden 15 ms'de gelen 20.000
kayıt, hiç meşgul olmayan bir yazıcı yüzünden kuyruğu doldurup yarısını kaybettiriyordu.
Toplama artık yazılacak kadar birikince kesiliyor. Regresyon testi:
`ABurstFasterThanTheDiskStillLosesNothing`.

---

## 5. P1 — Ayar kaydedilememesi *(doğrulandı, düzeltildi)*

`WriteJson` atomik yazımı zaten yapıyordu (geçici dosya + replace) ama her hatayı
yutuyordu. `ConfigStore.Save`/`SaveNetworks` artık sınıflandırılmış bir `ConfigSaveResult`
döndürür (erişim reddi / disk dolu / G-Ç / serileştirme). `ProtectionService.ReportSave`
durumu yalnızca **değiştiğinde** bildirir — düzenli denetim profil yazdığı için her
hatayı bildirmek, disk dolu kaldığı sürece birkaç dakikada bir aynı bandı ekrana koymak
olurdu. Ana ekranda "Bu oturumda uygulandı; kaydedilemedi" bandı ve **Yeniden kaydet**
düğmesi vardır.

Ağ profilleri sözlüğünün anlık kopyası artık dosya kilidinin *içinde* alınır: dosya
kilidi yazıcıyı korur, veriyi değil.

**Kanıt.** `ConfigSaveTests` (6): izin reddi, son sağlam dosyanın korunması, hızlı ardışık
değişikliklerde en yeni tercihin kazanması, artık geçici dosya kalmaması, ve profiller
değişirken yapılan yazımın bozulmaması.

---

## 6. P1 — Teşhis raporu *(yeni)*

Depoda eşdeğer bir dışa aktarma yoktu; arandı ve bulunamadı.

Günlük sayfasındaki **Tanı raporu kaydet** tek bir ZIP üretir: okunabilir `ozet.txt`,
şeması sürümlü `tani.json`, boyutu sınırlı `gunluk.txt` (512 KB, eski uçtan kırpılır).

- **Hiçbir ölçüm başlatmaz.** Prob yok, yük testi yok, bağlantı ayarı değişikliği yok.
- **Anlık görüntü düğmeye basıldığında** alınır, dosya iletişim kutusu açılmadan önce.
- Her ölçüm hangi motor oturumuna ve hangi ağa ait olduğunu taşır.
- Veri yoksa satır `ölçülmedi` der; sıfır yazılmaz. Paket kaybı yalnızca araç gerçekten
  ölçtüyse yazılır.
- **Maskeleme varsayılandır.** SSID, BSSID, MAC, IP, özel hedef, özel alan adı, kullanıcı
  adı ve profil yolu; serbest metin (istisna mesajları, günlük satırları) dahil.
  Takma adlar **sıra numarasıdır, hash değildir**: makul SSID uzayı sayılabilecek kadar
  küçüktür, dolayısıyla hash anonimleştirme değildir. Uygulamanın kendi sabitleri
  (127.0.0.1, genel çözümleyiciler) okunur kalır; günlük satırındaki saat adres sanılmaz.
- **Arşive girmeyenler:** ham ayar dosyaları ve günlük klasörünün tamamı.
- Arşiv hedefinin yanında kurulur ve ancak tamamlanınca yerine taşınır; iptal veya izin
  hatasında geriye yarım dosya kalmaz. **Hiçbir yere yüklenmez.**

**Kanıt.** `DiagnosticRedactionTests` (8), `DiagnosticReportWriterTests` (7).

---

## 7. P1 — Ping ekranı ve genel düzen

**Zaten çözülmüş olup korunanlar.** `LatencySituation`, §7.2'nin istediği ayrımların
tümünü hâlihazırda taşıyordu: `Incomplete` (ölçülemedi), `NotAvailableNow`/
`UnsupportedAdapter` (desteklenmiyor), `NoDifference` (fark yok), `RolledBack` (geriledi
ve geri alındı), `VerifiedGain` (doğrulandı). `LatencyRejection.Cause` ve
`LatencyOutcomeCauses.IsPerformanceEvidence`, engel yüzünden denenememiş adayın
"ölçüldü ve faydasız" diye kalıcı elenmesini zaten engelliyordu. Bunlar korunmuştur.

**Eksik olan ve eklenen:** akışın kendisi görünür değildi.
`Network/Latency/LatencyFlowSteps.cs` altı adımı üretir — hedef seçimi, bağlantı
doğrulaması, başlangıç ölçümü, aday denemeleri, karşılaştırma, kabul/geri alma. Satırlar
sonuçtan türetilir, koşu tarafından bildirilmez; dolayısıyla yalnızca gerçekten var olan
kanıtı iddia edebilirler. Yanına yüzde değil **geçen süre** konur: koşunun öngörülebilir
bir toplamı yoktur.

Ana ekranda **motorun çalışması ile hedefe erişimin doğrulanması ayrı iki satırdır.**
Servis, bir kontrolün en son ne zaman gerçekten geçtiğini (`LastVerifiedAt`), en son ne
zaman çalıştığından (`LastProbeAt`) ayrı tutar; durdurmak bunu temizler, çünkü artık var
olmayan bir motordan geçen trafiği tarif ediyordu.

Uzun ayarlar sayfasının başına ping, gönderim sınırı ve Vodafone bölümlerine giden üç
atlama düğmesi eklendi (1366×768 / %125'te üçü de katlamanın altında kalıyor). Düğme
oldukları için sekme sırasındadırlar; odak kaydırmayı izler.

**Kanıt.** `LatencyFlowStepTests` (13), `UiSurfaceTests` (8).

---

## 8. Test kapıları

`ViewModelBindingTests` öğe şablonlarının içini göremiyordu: oradaki bağlamalar öğeye
çözülür, ve iki tanesi yalnızca öğenin özellik adları görünüm modelinde de bulunduğu için
geçiyordu. Şablonlar artık görünüm modeli taramasından çıkarılıp bağlı oldukları
koleksiyondan çözülen **öğe tipine** karşı denetleniyor — kapsam azalmadı, arttı.
`UiSurfaceTests` ayrıca derlemenin göremediği iki hatayı yakalar: kod arkasında olmayan
bir `Click` işleyicisi (pencere kurulurken fırlatır) ve hiçbir şeyin tanımlamadığı bir
`ElementName` (sessizce hiçbir şey yapmaz).

Mevcut hiçbir test devre dışı bırakılmadı, hiçbir assertion gevşetilmedi. Bir testin
iddiası düzeltildi (`OnlyOneRunIsInsideTheWorkAtATime` → `RunsThatHonourTheir
CancellationNeverOverlap` + `ARunThatIgnoresItsCancellationDelaysNobodyAndWritesNothing`),
çünkü eski hâli koordinatörün *vermediği* bir garantiyi iddia ediyordu; yerine gerçek
garanti ve bilinçli ödünleşme ayrı ayrı test edildi.

Bu geçişte mevcut `ScrollingTests.NoComboBoxChangesItsValueOnAScrollGesture` testi
eklediğim bir birleşik giriş kutusunda gerçek bir kullanılabilirlik gerilemesi yakaladı
(kaydırma jesti değeri değiştiriyordu) ve düzeltildi.

---

## 9. Çalıştırılmayan kontroller

Bu ortam Linux'tur. Aşağıdakiler **çalıştırılmamıştır**:

- `--ui-selftest` ile gerçek WPF penceresinin açılması. WPF penceresi yalnızca Windows'ta
  oluşturulabilir. XAML *derlenmektedir* ve `scripts/tests/xaml-resources.tests.ps1`
  çözülmeyen ve ileri referanslı kaynakları yakalamaktadır, ancak bunlar bir pencerenin
  gerçekten çizildiğinin kanıtı değildir. CI'daki Windows işi bu kapıyı çalıştırır.
- Gerçek NIC, WinDivert sürücüsü, gerçek DNS/QoS/adaptör değişiklikleri. Sürücü yükü
  olmadan yapılan başarılı derleme, çalışan paketin kanıtı sayılmamıştır.
- Gerçek monitör DPI geçişi, oturum açılışı, tepsi etkileşimi, %100/125/150/200 ölçek ve
  1366×768'de görsel doğrulama. Ekran görüntüsü alınamamıştır.
- Gerçek internet üzerinden DoH sağlayıcılarına karşı gecikme ölçümü. Bu yüzden DoH
  paylaşımı için **süre iyileşmesi iddia edilmemiştir** (aşağıya bakınız).
- Inno Setup paketi ve sürücü imza doğrulaması.

### DoH paylaşımı — ölçülen ve ölçülmeyen

Aynı soruyu soran 50 eşzamanlı istemci, her isteğe 40 ms'de yanıt veren sahte bir
sağlayıcıya karşı:

| | Upstream istek | Toplam süre |
| --- | --- | --- |
| Paylaşımsız (istemci başına ayrı çözümleyici) | 50 | 41–77 ms |
| Paylaşımlı (bu yapı) | 1 | 41–42 ms |

Ölçülen kazanç **istek sayısıdır: 50 → 1.** Süre bu koşumda değişmemektedir, çünkü sahte
sağlayıcının istek başına maliyeti yoktur ve 50 istek paralel gitmektedir. Gerçek bir
sağlayıcıda istek başına maliyet (TLS/HTTP2 akışı, sağlayıcı hız sınırları) vardır ama
**bu ölçülmemiştir**, dolayısıyla bir yüzde verilmemektedir.

---

## 10. Son durum

| Kapı | Referans commit | Bu dal |
| --- | --- | --- |
| `dotnet test` | 799 / 0 | 901 / 0 |
| `dotnet build DpiBypass.slnx -c Release` | başarılı | başarılı |
| `dotnet publish` | başarılı | başarılı |
| `scripts/tests/install.tests.ps1` | başarılı | başarılı |
| `scripts/tests/latency-harness.tests.ps1` | başarılı | başarılı |
| `scripts/tests/xaml-resources.tests.ps1` | başarılı | başarılı |

Komutlar Linux'ta `-p:EnableWindowsTargeting=true` ile çalıştırılmıştır; hedef çatı
`net10.0-windows` olarak korunmuştur.

Korunanlar: DPI koruması, alan adı yönetimi, otomatik yöntem seçimi, DNS seçenekleri,
ping ölçümü/iyileştirme, Trafik Koruması, Vodafone modu (işlev tanımı değiştirilmedi,
kaldırılmış paket müdahaleleri geri getirilmedi), otomatik başlatma, tepsi, CLI ve
kurtarma yolları. Üretici bilgisi `Atom Gamer Arda A.G.A` olarak kalmıştır. Kullanıcı
ayarları, ağ profilleri ve izin tercihleri için dosya biçimi değiştirilmemiştir.
