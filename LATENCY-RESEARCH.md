# Gecikme araştırması — kaynaklar, kararlar ve geri alma

Bu belge, "Ping düşürme" özelliğinde **ne yapıldığının ve neden yapıldığının**
denetlenebilir kaydıdır. Her satır resmî bir kaynağa dayanır. Blog yazıları,
forum "gaming tweak" listeleri ve kaynağı belirsiz registry paketleri kaynak
olarak kullanılmamıştır.

Erişim tarihi: **30 Ağustos 2026**; V2 kaynakları **31 Ağustos 2026** tarihinde
yeniden açılıp doğrulandı. Sürümler değiştiğinde bu belge de güncellenmelidir;
kod, burada yazılmayan hiçbir ayarı değiştirmez.

V2'de değişen kararlar `LATENCY-AUDIT-V2.md` içinde bulgu bazında işaretlidir.

---

## 1. Kullanılan resmî kaynaklar

| # | Kaynak | Konu |
| --- | --- | --- |
| R1 | <https://learn.microsoft.com/windows-hardware/drivers/network/interrupt-moderation> | Interrupt moderation ve RTT ilişkisi |
| R2 | <https://learn.microsoft.com/windows-hardware/drivers/network/enumeration-keywords> | Standart NDIS enum keyword'leri ve geçerli değerleri |
| R3 | <https://learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-rsc> | `*RscIPv4` / `*RscIPv6` |
| R4 | <https://learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management> | `*EEE`, `*DeviceSleepOnDisconnect` |
| R5 | <https://learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-ndis-selective-suspend> | `*SelectiveSuspend`, `*SSIdleTimeout` |
| R6 | <https://learn.microsoft.com/windows-server/networking/technologies/hpn/hpn-hardware-only-features> | Checksum offload, interrupt moderation, RSC, LSO |
| R7 | <https://learn.microsoft.com/windows-server/networking/technologies/network-subsystem/net-sub-choose-nic> | RSS ve RSC gecikme/verim dengesi |
| R8 | <https://learn.microsoft.com/powershell/module/netadapter/set-netadapterpowermanagement> | `D0PacketCoalescing`, `SelectiveSuspend`, `-NoRestart` |
| R9 | <https://learn.microsoft.com/powershell/module/netadapter/> | NetAdapter cmdlet ailesi |
| R10 | <https://learn.microsoft.com/powershell/module/netqos/new-netqospolicy> | `New/Get/Remove-NetQosPolicy`, `PolicyStore`, `ThrottleRateActionBitsPerSecond` |
| R11 | <https://learn.microsoft.com/windows-server/networking/technologies/qos/qos-policy-top> | Policy-based QoS: eşleşme koşulları, DSCP, throttle |
| R12 | <https://learn.microsoft.com/windows-server/networking/technologies/qos/qos-policy-works> | QoS Inspection Module + Pacer.sys akış mekanizması |
| R13 | <https://www.rfc-editor.org/rfc/rfc2681> | IPPM Round-trip Delay, Type-P, kayıp/gecikme eşiği |
| R14 | <https://www.rfc-editor.org/rfc/rfc7679> | IPPM One-way Delay, kalibrasyon ve raporlama gereksinimleri |
| R15 | <https://learn.microsoft.com/powershell/module/netadapter/set-netadapteradvancedproperty> | `-NoRestart` ve gelişmiş ayarların ne zaman etkinleştiği |
| R16 | <https://learn.microsoft.com/powershell/module/netadapter/restart-netadapter> | Kontrollü bağdaştırıcı yeniden başlatma |
| R17 | <https://learn.microsoft.com/powershell/module/netadapter/get-netadapterrsc> | RSC'nin *operational* durumu (keyword değil) |
| R18 | <https://learn.microsoft.com/powershell/module/netadapter/get-netadapterlso> | LSO v2'nin operational durumu |
| R19 | <https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getpertcpconnectionestats> | Var olan TCP bağlantısının RTT'si (`TcpConnectionEstatsPath`) |
| R20 | <https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-setpertcpconnectionestats> | EStats toplamayı açma; yönetici gereksinimi |
| R21 | <https://learn.microsoft.com/windows-server/networking/technologies/qos/qos-policy-architecture> | QoS Inspection Module ↔ Pacer.sys mimarisi |
| R22 | <https://reqrypt.org/windivert-doc.html> | WinDivert 2.2 `WINDIVERT_LAYER_FLOW`, `SNIFF｜RECV_ONLY`, kısıtlar |
| R23 | <https://learn.microsoft.com/windows/win32/winmsg/getsystemmetrics> | `SM_REMOTESESSION` — uzak oturum tespiti |

---

## 2. Ölçüm metodolojisi

### 2.1 Type-P (R13, R14)

RFC 2681: *"The value of Type-P-Round-trip-Delay could change if the protocol
(UDP or TCP), port number, size, or arrangement for special treatment …
changes."*

**Uygulandı.** Bir deneyin iki yarısı yalnız aynı adres, aynı protokol ve aynı
port ile ölçüldüyse çıkarılabilir (`LatencyPair.HasSameMeasurementPath`,
`LatencyEndpoint.Key`). Hedef deney başında bir kez çözülür ve sabitlenir;
hiçbir kol yeniden DNS çözümlemesi yapmaz.

**Risk / geri alma:** yok — bu bir ölçüm kuralıdır, sistemde hiçbir şey
değiştirmez.

### 2.2 Kayıp ile büyük gecikmenin ayrımı (R13, R14)

Her iki RFC de aynı zorunluluğu koyar: *"the threshold (or methodology to
distinguish) between a large finite delay and loss MUST be reported."*

**Uygulandı.** `LatencyProbeRequest.TimeoutMilliseconds` (varsayılan 900 ms)
eşiktir; bu değer rapora ve JSON çıktısına yansır, kayıp gecikme
istatistiklerine karıştırılmaz — yanıtsız probe'lar median/p95'e dâhil edilmez,
ayrı bir `PacketLossPercent` olarak raporlanır.

### 2.3 Yüzdelik ve örnek sayısı

RFC'ler istatistiğin hangi örnek üzerinde hesaplandığının raporlanmasını ister.

**Uygulandı.** p99, iki kolda da **en az 100 geçerli yanıt** yoksa karar metriği
olamaz (`LatencyEvaluationOptions.MinimumRepliesForP99`) ve JSON çıktısında
`null` olarak görünür. Varsayılan benchmark 40, derin geçiş 120 probe'dur.

---

## 3. NIC müdahaleleri

Her müdahale şu zinciri geçer: **yetenek tespiti → snapshot → uygula →
geri oku → eşli A/B ölçüm → kabul veya geri al.** Hiçbiri "iyi tweak"
varsayımıyla uygulanmaz.

### 3.1 `*InterruptModeration` → 0 — **uygulandı**

R1: *"when interrupt moderation is enabled, receiving a packet doesn't generate
an immediate interrupt and therefore the perceived roundtrip time for a
particular packet becomes larger than the average time. To allow accurate
measurement of roundtrip time for a packet, NDIS provides the ability to disable
and enable interrupt moderation on demand."*

R6: *"Generally, packet processing is more efficient with Interrupt Moderation
enabled. High performance or low latency applications may need to evaluate the
impact of disabling or reducing Interrupt Moderation."*

- **Kapsam:** tüm protokoller. **Maliyet:** CPU (paket başına kesme).
- **Değer kaynağı:** R2, `*InterruptModeration` 0 = Disabled, 1 = Enabled.
- **Karar:** aday olarak eklendi, CPU-duyarlı işaretlendi — kabul için serbest
  bir değişikliğin **iki katı** kazanç göstermek zorunda.
- **Geri alma:** `Set-NetAdapterAdvancedProperty -RegistryKeyword
  '*InterruptModeration' -RegistryValue <özgün>`; özgün değer snapshot'a
  yazıldıktan **sonra** yazılır.

**Vendor "Low / Medium / High" seviyeleri kasten kullanılmadı.** Standart
keyword yalnız 0/1'dir (R2); seviyeler standart dışı, üreticiye özel
keyword'lerdir ve anlamları ancak yerelleştirilmiş `DisplayValue` okunarak
tahmin edilebilir. Bu proje yerelleştirilmiş metin okumaz, dolayısıyla
seviyelere dokunmaz.

### 3.2 `*RscIPv4` / `*RscIPv6` → 0 — **uygulandı**

R7: *"This approach can affect latency with benefits mostly seen in throughput
gains. … ensure that RSC is on (this is the default setting), unless you have
specific workloads (for example, low latency, low throughput networking) that
show benefit from RSC being off."*

- **Kapsam:** yalnız TCP (RSC aynı TCP akışının paketlerini birleştirir). UDP
  hedefinde aday olarak **sunulmaz**.
- **Ek koşul:** `Get-NetAdapterRsc` `IPv4Operational` / `IPv6Operational`
  `false` diyorsa aday değildir — çalışmayan bir özelliği kapatmak dakikalar
  harcayıp hiçbir şey kanıtlamaz.
- **IPv4 ve IPv6 ayrı adaylardır**, özgün değerleri ayrı yakalanır ve ayrı geri
  yüklenir.
- **Geri alma:** kendi registry keyword'ünün özgün değeri.

### 3.3 `*RSS` → 1 — **uygulandı, dar koşullu**

R7: RSS *"distributes incoming network I/O packets among logical processors"*.

- Yalnız **kablolu Ethernet**, **en az 4 mantıksal işlemci** ve keyword şu anda
  `0` ise sunulur. Kablosuz kartlarda RSS donanım desteği **varsayılmaz**
  (keyword'ün varlığı destek kanıtı değildir).
- `Get-NetAdapterRss` zaten `Enabled` diyorsa aday değildir.
- **Restart riski:** `MayNeedRestart = true`. Değer `-NoRestart` ile yazılır ve
  hemen geri okunur; sürücü canlı almadıysa aday sessizce uygulanmaz, geri alınır.

### 3.4 `*EEE` → 0 — **uygulandı**

R4: `*EEE` — *"A value that describes whether the device should enable IEEE
802.3az energy-efficient ethernet."* Kanonik standart keyword; 0 = Disabled.

- **Maliyet:** güç. Bataryadayken kullanıcı açıkça izin vermedikçe sunulmaz.
- **Geri alma:** özgün registry değeri.
- Bu keyword yalnız **kanonik `*EEE`** eşleşmesiyle kullanılır. "Green Ethernet",
  "Enerji Verimli Ethernet" gibi yerelleştirilmiş görünen adlara göre rastgele
  property değiştirilmez.

### 3.5 `*LsoV2IPv4` / `*LsoV2IPv6` → 0 — **tanımlı, ama hiçbir tur denemiyor**

> **V2 düzeltmesi.** Bu başlık önce "yalnız yük altındaki lane'de" diyordu; bu
> doğru değildi. `IncludeThroughputSensitive` yalnız boştaki turda ve `false`
> olarak veriliyor, yük altındaki lane ise NIC anahtarı değil hat ve QoS ölçüyor.
> Yani bu iki anahtar **hiçbir zaman aday olmuyor.** Katalogdan çıkarılmadılar
> çünkü çıkarmak, eski bir snapshot'taki LSO değerinin geri yüklenmesini de
> engellerdi.

R6: LSO *"allows an application to pass a large block of data to the NIC, and
the NIC breaks the data into packets"*. Ayrıca R6: LSO kapatmadan tüm checksum
hesabı kapatılamaz — yani LSO kapatmanın CPU maliyeti gerçektir.

- Oyun paketleri MTU altındadır; boştaki gecikme koşusunda LSO'nun etkileyeceği
  hiçbir blok yoktur. Bu yüzden **idle lane'de aday değildir**
  (`IncludeThroughputSensitive = false`).
- Yalnız toplu gönderim ölçülen lane'de, TCP kapsamıyla ve CPU koruması ile
  aday olur.

### 3.6 `SelectiveSuspend` — **V2'de aday olmaktan çıkarıldı**

R5: NDIS boşta kalan bağdaştırıcıyı askıya alır; `*SSIdleTimeout` varsayılanı
**5 saniyedir** ve NDIS bu süreyi %30 toleransla ölçer.

Etkisi **sürekli trafikte değil**, uzun boşluktan sonraki ilk pakettedir. V1 bunu
açıklama metnine yazıyor ama yine de sürekli probe gönderen bir steady-state
deneyiyle ölçüyordu — yani deneyin göremeyeceği bir şeyi ölçmek için dakikalarca
çalışıyor, sonra "kazanç yok" diyordu. Bu doğru sonucun pahalı yoluydu.

**V2:** aday listesinden çıkarıldı (`AdapterInterventionCatalog.WritablePowerProperties`
artık boş). Ayrı bir *first-packet* deneyi yazılmadı; görev tanımı bu iki
seçenekten birini istiyordu ve çıkarmak, ölçülmemiş bir ayarı "optimize edildi"
diye raporlamama kuralıyla daha uyumludur. Eski snapshot'lar için
`RestorablePowerProperties` içinde kalır.

### 3.7 `D0PacketCoalescing` — **V2'de aday olmaktan çıkarıldı**

R8: *"This reduces the number of receive interrupts by coalescing random
broadcast or multi-cast packets."*

**Yayın/çoklu yayın** paketlerini birleştirir; tekil (unicast) oyun trafiğine
doğrudan bir mekanizması yoktur. V1 bunu açıklama metninde söylüyor ama yine de
genel bir oyun RTT müdahalesi olarak sunuyordu.

**V2:** aday listesinden çıkarıldı, `RestorablePowerProperties` içinde kaldı.

### 3.7.1 Bir ayarın gerçekten etkinleşmesi (R15, R16, R17, R18)

R15, `-NoRestart` için açık: *"Indicates that the cmdlet does not restart the
network adapter after completing the operation. **Many advanced properties
require restarting the network adapter before the new settings take effect.**"*

V1, değeri `-NoRestart` ile yazıp aynı registry değerini geri okuyor ve bunu
"canlı uygulandı" sayıyordu. Bu, sürücünün yeni değerle çalıştığını **kanıtlamaz**;
sonraki A/B ölçümü eski davranışı iki kez ölçüp farkı yeni değere yazar.

**V2 zinciri** (`WindowsLatencyAdapterController`):

1. `-NoRestart` ile yaz, registry'den geri oku. Eşleşmiyorsa → `Refused`.
2. Mümkünse **operational** durumu sor: `Get-NetAdapterRsc` (R17),
   `Get-NetAdapterRss` (R7), `Get-NetAdapterLso` (R18). İstenen durumu
   bildiriyorsa → `OperationallyVerified`, yeniden başlatma **gerekmez**.
3. Operational sorgusu olmayan anahtarlar (`*InterruptModeration`, `*EEE`) için
   tek dürüst yol miniport'u yeniden başlatmaktır. Kullanıcı onayı yoksa →
   `RestartRequired` ve **ölçüm yapılmaz.**
4. Onay varsa `Restart-NetAdapter` (R16); sonra aynı GUID, link up, IPv4 adresi
   ve varsayılan rota beklenir. Gelmezse → `LinkNotRestored` ve geri alma.
5. Yeniden başlatmadan sonra arabirim indeksi, ilk atlama ve erişim noktası
   karşılaştırılır, hedefe erişim doğrulanır. Değişmişse tur iptal edilir.

Yalnız `OperationallyVerified` ve `AdapterRestarted` ölçüme izin verir
(`LatencyApplyResult.IsEffective`). **Uzak oturumda** (R23, `SM_REMOTESESSION`)
yeniden başlatma hiçbir koşulda yapılmaz: oturumu taşıyan bağdaştırıcıyı yeniden
başlatmak oturumu bitirir.

**Risk:** birkaç saniyelik bağlantı kesintisi. **Geri alma:** snapshot yazmadan
önce diske atomik olarak kaydedilir; başarısız her yolda özgün değer geri yazılır.

### 3.8 `DeviceSleepOnDisconnect` — **aday olmaktan çıkarıldı**

R4: *"A value that describes whether the device should be enabled to put the
device into a low-power state (sleep state) **when media is disconnected**."*

Bağlantı aktifken RTT ile ilgisi yoktur. Önceki sürüm bunu bir gecikme adayı
olarak deniyordu; **kaldırıldı.** Yalnız eski bir snapshot taşıyan makineler
için **geri yüklenebilir** listede kalır
(`AdapterInterventionCatalog.RestorablePowerProperties`).

### 3.9 Checksum offload — **hiçbir koşulda kapatılmaz**

R6: *"Address Checksum Offloads should ALWAYS be enabled no matter what workload
or circumstance. … Checksum offloading is also required for other stateless
offloads to work including receive side scaling (RSS), receive segment coalescing
(RSC), and large send offload (LSO)."*

`*IPChecksumOffloadIPv4`, `*TCPChecksumOffloadIPv4/6`,
`*UDPChecksumOffloadIPv4/6`, `*TCPUDPChecksumOffloadIPv4/6` yasak listededir ve
hem C# kataloğu hem PowerShell betiği bunları reddeder. Bir birim testi bu
keyword'lerin hiçbir aday listesinde belirmediğini kanıtlar.

### 3.10 Hiç eklenmeyenler ve nedenleri

| Yapılmadı | Neden |
| --- | --- |
| `TcpAckFrequency`, `TCPNoDelay`, Nagle registry listeleri | Belgelenmiş bir Microsoft önerisi yok; global TCP davranışını her uygulama için değiştirir |
| `NetworkThrottlingIndex`, `SystemResponsiveness` | Multimedya zamanlayıcı ayarları; ağ gecikmesiyle nedensel bağı belgelenmemiş |
| HPET / timer resolution | Sistem geneli zamanlayıcı; güç ve zamanlama yan etkileri, ağ RTT'siyle bağı yok |
| Rastgele MTU | Yol MTU keşfini bozar; kara delik bağlantı üretebilir |
| IPv6 kapatma | Microsoft tarafından önerilmez; erişilebilirliği bozar |
| DNS değişikliğini "ping düştü" diye sunmak | DNS çözümleme süresi RTT değildir; ayrı ölçülür, ayrı raporlanır |
| Checksum offload kapatma | R6 (yukarıda) |
| Firewall / güvenlik servisi / Windows Update kapatma | Güvenlik özelliğidir; gecikme özelliği değil |
| Herkese "Yüksek performans" güç planı | Kullanıcının güç tercihini ezer; batarya maliyeti ölçülmeden dayatılamaz |
| VPN/proxy kurup "yerel optimizasyon" demek | Yerel optimizasyon değildir |

### 3.11 Interface metric / çoklu adapter — **yalnız tanılama**

Birden çok aktif fiziksel adapter varsa hangisinin kullanıldığı raporlanır.
`Set-NetIPInterface -InterfaceMetric` ile **otomatik rota değişikliği
yapılmaz**: bu yazı gerçek Windows üzerinde doğrulanmadan gönderilmeyecek kadar
risklidir (yanlış metrik bağlantıyı tamamen koparabilir). Bu bilinçli bir
kapsam dışı bırakmadır, unutulmuş bir madde değil.

---

## 4. Yük altındaki gecikme ve Traffic Guard

### 4.1 Policy-based QoS nasıl çalışıyor (R11, R12)

R12: yeni bir transport endpoint açıldığında QoS Inspection Module ilkelerle
eşleştirir, eşleşirse **Pacer.sys**'e DSCP değerini ve throttle ayarını taşıyan
bir akış oluşturtur; gönderilen paketler bu akış numarasıyla işaretlenir ve
Pacer.sys gerekiyorsa zamanlar.

R11: ilke **giden** trafiğe uygulanır ve şunlarla eşleşebilir: gönderen
uygulama ve dizin yolu, kaynak/hedef IPv4-IPv6 adres veya öneki, protokol
(TCP/UDP), kaynak/hedef port veya port aralığı.

R10: `New-NetQosPolicy -ThrottleRateActionBitsPerSecond`, `-DSCPAction 0..63`,
`-PolicyStore ActiveStore` (*"If a policy is stored in ActiveStore, then the
policy does not persist after restart."*).

### 4.2 Bu projede ne yapıldı

- Yalnız **`DPIBypass.Latency.` ön ekli** ilkeler oluşturulur. Kaldırma yolu bu
  ön eki taşımayan hiçbir adı kabul etmez — hem C# tarafında hem betikte.
- Depo **`ActiveStore`**: yeniden başlatmadan sonra kalmaz. Crash sonrası
  makine temiz açılır; mod açıksa ilke yeniden ölçülüp yeniden kurulur.
- Kullanıcının veya GPO'nun ilkesi **hiçbir koşulda değiştirilmez veya
  silinmez.** Rakip bir ilke (bir `Default` ilke ya da başka bir throttle)
  bulunursa otomatik müdahale atlanır ve kullanıcıya bildirilir.
- Snapshot şunları taşır: oluşturan sürüm, ilke adı, eşleşme koşulu, throttle
  değeri, policy store, oluşturma zamanı, ilgili ağ/profil ve ilkenin bizden
  önce var olup olmadığı.
- İlke, **ölçülen** bir kuyruklanma azalması göstermezse silinir. Paket kaybı
  artarsa silinir. Gönderim hızı fazla düşerse silinir.
- **DSCP tek başına kazanç sayılmaz.** Router'ın işaretlemeyi sınıflandırıp
  sınıflandırmadığı bu uçtan görülemez; yalnız yük altındaki RTT'nin gerçekten
  düşmesi kazançtır.

### 4.3 Windows üzerinde doğrulanması gerekenler — **NOT RUN**

Bu depoda Windows çalıştıran bir test ortamı yoktur; QoS davranışı **kod
tarafından varsayılmaz, ölçülür.** Bu mimari sayesinde platform beklendiği gibi
davranmazsa ilke kendiliğinden silinir ve hiçbir yanlış iddia üretilmez. Yine de
gerçek Windows 10/11 üzerinde şunlar doğrulanmalıdır ve **bu sürümde hiçbiri
çalıştırılmamıştır**:

1. `New-NetQosPolicy -AppPathNameMatchCondition <exe> -ThrottleRateActionBitsPerSecond N -PolicyStore ActiveStore`
   ilkesinin gerçekten gönderim hızını sınırlaması.
2. `Get-NetQosPolicy -PolicyStore ActiveStore` ile her koşul ve eylemin geri
   okunabilmesi (V2 read-back tam alan karşılaştırması yapar).
3. `Remove-NetQosPolicy` sonrası sınırın kalkması.
4. **Tek ve çoklu TCP akışında** throttle'ın uygulama toplamına mı, akış başına
   mı davrandığı. R10 ve R21 bunu söylemiyor; cevap ölçümle bulunmalıdır.
5. İlke oluşturulmadan **önce** açılmış bir bağlantının ilkeye tabi olup
   olmadığı. R21'e göre eşleşme transport uç noktası oluşurken yapılır, yani
   olmaması beklenir; V2 kodu bunu zaten varsaymayıp yeni akış bekler.
6. Domain'e katılmış bir makinede GPO ilkesiyle birlikte davranış.

`scripts/integration/latency-windows.ps1` bu soruların ilk üçünü ve NIC
tarafını kaydeder; 4 ve 5 gerçek bir aktarım gerektirdiği için harness'in
kendi raporunda `notRun` olarak yazılır. **Bu sürümde harness çalıştırılmamıştır.**

### 4.3.1 V2'de kapatılan üç QoS boşluğu

- **İsimle doğrulama.** V1, `Get-NetQosPolicy | Where Name -eq $name` sonucunu
  "oluşturuldu" sayıyordu. Bu yalnız adı kanıtlar. V2, `AppPathName`,
  `IPProtocol`, `IPDstPrefix`, `IPDstPort`, `ThrottleRateAction`, `DSCPValue`,
  `Precedence`, `PolicyStore` ve ad alanı sahipliğini **tek tek** karşılaştırır
  (`WindowsQosController.DescribeMismatch`).
- **Eski akış üzerinde ölçüm.** R21: eşleşme transport uç noktası oluşturulurken
  yapılır. V1 ilkeyi çalışan aktarımın *altında* oluşturup aynı aktarımı
  ölçüyordu — yani sınırsız akışı ölçüp farkı ilkeye yazıyordu. V2 aktarımı
  durdurtur, ilkeyi oluşturur, **yeni bir akış** görene kadar bekler (WinDivert
  FLOW, R22) ve ancak sonra ölçer. Akış gelmezse sonuç üretilmez
  (`TrafficGuardStatus.NeedsNewConnection`).
- **Sınırın gerçekten uygulandığı.** Ölçülen bayt hızı sınırla tutarlı değilse
  (`RateHonoured`) o tur, o sınır hakkında veri sayılmaz.

### 4.3.2 Sabit %85 yerine ölçülen cap (V2)

V1 `ThrottleShare = 0.85` sabitini kullanıyordu. Kuyruğun nerede olduğu, ne kadar
derin olduğu ve boşalma hızının ne kadar pay gerektirdiği operatör ekipmanının
özelliğidir; buradan bilinemez. V2 birkaç cap uygular ve ölçer
(`TrafficGuardCapPlanner`), sıralamayı **yük altındaki p95 → p99 → kuyruklanma
farkı → jitter → kayıp → korunan throughput** önceliğiyle yapar, ve seçilen cap'i
**aramada kullanılmayan ayrı bir doğrulama turunda** tekrar sınar. İki mod
vardır: *Dengeli* (throughput tabanı %70) ve *En düşük gecikme* (taban %40, kayıp
kullanıcıya gösterilir).

### 4.4 M-Lab / NDT7 — **entegre edilmedi**

Yük üretmek için M-Lab NDT7 değerlendirildi ve **kullanılmadı.** Gerekçe:
M-Lab ölçüm verileri kamuya açıktır ve bilgilendirilmiş onam ile açık bir
gizlilik bildirimi gerektirir; bu uygulamanın mevcut onay akışı bunu
karşılamıyor. Bu koşullar sağlanmadan üçüncü tarafa veri gönderen bir test
eklenmemiştir.

**Bunun yerine:** yük **kullanıcının kendi trafiğinden** gözlemlenir. Uygulama
hiçbir bayt göndermez; kullanıcı zaten yapacağı indirmeyi/gönderimi başlatır,
uygulama bağdaştırıcı sayaçlarından hattın gerçekten dolduğunu görür ve o
pencerede RTT ölçer. Trafik gelmezse cevap "ölçülmedi"dir, tahmin değil.
Kapasiteyi kullanıcı biliyorsa elle girebilir (`ManualUplinkMbps`,
`ManualDownlinkMbps`).

**V2 ekler:** "yük altında" artık sabit bir eşik ya da görülen en yüksek hızın
çeyreği değildir. Kapasite, **yükselip düzleşen bir rampadan** öğrenilir
(`LinkCapacityRamp`: art arda üç pencere birbirine %15 içinde ve tepe değerin
%90'ının üstünde), yön başına güven ve zaman damgasıyla saklanır, ve doygunluk
**ölçülmüş** kapasitenin %85'idir. Kapasite güveni düşükse sonuç "ölçülmedi"dir;
"bufferbloat yok" değildir. Otomatik yük sağlayıcısı **hiç eklenmediği** için
metered/mobil bağlantılarda kapatılacak bir otomatik trafik de yoktur.

### 4.5 WinDivert tabanlı shaper — **eklenmedi**

Windows QoS'un yetersiz kaldığı kanıtlanmadan kullanıcı alanında yeni bir paket
zamanlayıcı eklenmemiştir. Görev tanımındaki ship kriterleri (per-flow adalet,
ACK koruması, crash'te trafiğin kesilmemesi, ölçülmüş kazanç) gerçek Windows
entegrasyon testi olmadan karşılanamaz. V2'de bu karar değişmedi: QoS'un
yetersizliği hâlâ ölçülmemiştir, dolayısıyla shaper'ın gerekçesi de yoktur.

### 4.6 WinDivert FLOW katmanı — **yalnız pasif keşif için eklendi (R22)**

Windows'un UDP tablosu bir soketin **yalnız yerel** adresini ve portunu bildirir;
UDP soketinin bildirecek bir uzak ucu yoktur. V1 bu yüzden UDP oynayan oyunların
sunucusunu hiç bulamıyor ve kullanıcıdan adresi elle yazmasını istiyordu.

R22'nin FLOW katmanı bu soruyu tam olarak cevaplar:
`WINDIVERT_EVENT_FLOW_ESTABLISHED` / `..._DELETED`, süreç kimliği ve tam beşli ile,
TCP ve UDP için. Katman `SNIFF | RECV_ONLY` ile açılmak **zorundadır** — yani
handle hiçbir şeyi engelleyemez, değiştiremez, enjekte edemez.

- **Paket yolu etkilenmez.** FLOW katmanı paket değil olay taşır; handle yalnız
  derin test sürerken açıktır ve bitince kapanır.
- **DPI motoruyla çakışma yok.** Farklı katman, farklı öncelik (100; motor 1000
  ve 1001'de, Network katmanında).
- **Belgelenmiş kısıt:** *"the WINDIVERT_LAYER_FLOW layer cannot capture flow
  events that occurred before the handle was opened."* Bu gizlenmez: gözlem
  başladıktan sonra yeni akış görülmediyse kullanıcıya oyuna yeniden bağlanması
  söylenir.
- **Gizlilik:** akışlar yalnız bir keşif turu boyunca bellekte tutulur, diske
  yazılmaz; günlüğe IP veya süreç yolu düşmez.

### 4.7 Gerçek uygulama RTT'si — iki araç (R19, R20)

Rastgele bir oyun sunucusuna saniyede birkaç TCP el sıkışması açmak, o sunucunun
anti-abuse kurallarının tam olarak durdurmak için var olduğu trafik şeklidir; ve
el sıkışma süresi zaten oyunun oturum içi RTT'si değildir.

- **TCP EStats (R19, R20).** `SetPerTcpConnectionEStats` ile
  `TcpConnectionEstatsPath` açılır, `GetPerTcpConnectionEStats` yığının
  **zaten ölçtüğü** `SmoothedRtt` değerini verir. Hiç paket gönderilmez.
  R20 iki kısıtı açıkça yazar: çağrı yönetici gerektirir, ve *"the caller should
  check the EnableCollection field … and if it is not TRUE, then the caller
  should ignore the data"* — kod ikisini de uygular ve açılamazsa **hiç örnek
  üretmez**, uydurma bir yedeğe düşmez. IPv4 ile sınırlıdır.
- **Minecraft Java durum sorgusu.** Server List Ping alışverişinin sonundaki
  Ping (0x01) paketi sunucu tarafından aynen yankılanır; bu, oyunun kendi
  çokoyunculu listesinde gördüğü sayının ta kendisidir. Tek bağlantı üzerinden
  ölçülür, yani sunucu bir durum bağlantısı görür. **Genelleştirilmedi:** başka
  oyunların el sıkışmaları belgesizdir ve uydurulmuş bir tanesi "ping gibi görünen
  ama ping olmayan" bir sayı üretir.

Ölçülemeyen protokoller için sonuç **rota referansı** olarak etiketlenir
(`LatencyEndpoint.RouteReferenceOnly`) ve oyunun ping'i olarak sunulmaz.

---

## 5. Gizlilik

- Profil dosyasında SSID, BSSID, IP adresi veya tam işlem yolu **tutulmaz**;
  ağ, erişim noktası ve hedef kısa hash'lerle temsil edilir.
- Günlüklerde genel IP, SSID, BSSID ve tam işlem yolu yazılmaz.
- Hiçbir ölçüm sonucu dışarı gönderilmez; tüm dosyalar
  `C:\ProgramData\DPI Bypass\` altında yereldir.
- **V2:** WinDivert FLOW katmanından okunan akışlar (süreç kimliği, yerel/uzak
  IP ve port) yalnız çalışan bir keşif turu boyunca bellekte tutulur; hiçbir
  dosyaya yazılmaz ve günlüğe düşmez. Sabitlenen uç nokta ayarlarda
  `adres:port` olarak saklanır çünkü kullanıcının bilinçli seçimidir.
- Traffic Guard'ın eşleştirdiği uygulama yolu yalnız QoS ilkesinin kendi
  eşleşme koşuluna girer; profil dosyasına yazılmaz.

---

## 6. Fiziksel sınırlar

Aşağıdakiler bu yazılımla **düşürülemez** ve arayüz bunu böyle söyler:

- Sunucuya olan coğrafi mesafenin ışık hızı payı.
- ISP omurgasındaki ve peering noktalarındaki rota gecikmesi.
- Uzak sunucunun kendi tick/işleme gecikmesi.
- Karşı tarafın (indirme yönü) doldurduğu operatör kuyruğu — yerel bir hız
  sınırı oraya zamanında ulaşmaz.
- Oyunun kendi ağ modeli, interpolasyon ve tick rate'i.
