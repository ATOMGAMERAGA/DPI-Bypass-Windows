# Gecikme araştırması — kaynaklar, kararlar ve geri alma

Bu belge, "Ping düşürme" özelliğinde **ne yapıldığının ve neden yapıldığının**
denetlenebilir kaydıdır. Her satır resmî bir kaynağa dayanır. Blog yazıları,
forum "gaming tweak" listeleri ve kaynağı belirsiz registry paketleri kaynak
olarak kullanılmamıştır.

Erişim tarihi: **30 Ağustos 2026**. Sürümler değiştiğinde bu belge de
güncellenmelidir; kod, burada yazılmayan hiçbir ayarı değiştirmez.

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

### 3.5 `*LsoV2IPv4` / `*LsoV2IPv6` → 0 — **yalnız yük altındaki lane'de**

R6: LSO *"allows an application to pass a large block of data to the NIC, and
the NIC breaks the data into packets"*. Ayrıca R6: LSO kapatmadan tüm checksum
hesabı kapatılamaz — yani LSO kapatmanın CPU maliyeti gerçektir.

- Oyun paketleri MTU altındadır; boştaki gecikme koşusunda LSO'nun etkileyeceği
  hiçbir blok yoktur. Bu yüzden **idle lane'de aday değildir**
  (`IncludeThroughputSensitive = false`).
- Yalnız toplu gönderim ölçülen lane'de, TCP kapsamıyla ve CPU koruması ile
  aday olur.

### 3.6 `SelectiveSuspend` → Disabled — **uygulandı, mekanizması dürüst yazıldı**

R5: NDIS boşta kalan bağdaştırıcıyı askıya alır; `*SSIdleTimeout` varsayılanı
**5 saniyedir** ve NDIS bu süreyi %30 toleransla ölçer.

- Yani etkisi **sürekli trafikte değil**, uzun boşluktan sonraki ilk paketdedir.
  Bu, açıklama metnine aynen yazıldı; ölçüm sistemi de bunu göremeyeceği için
  aday çoğu makinede reddedilir — bu doğru sonuçtur.
- **Maliyet:** güç. `Set-NetAdapterPowerManagement -SelectiveSuspend` (R8)
  kullanılır; bu yol miniport'u yeniden başlatmaz.

### 3.7 `D0PacketCoalescing` → Disabled — **uygulandı, sınırı yazıldı**

R8: *"This reduces the number of receive interrupts by coalescing random
broadcast or multi-cast packets."*

- Yani **yayın/çoklu yayın** paketlerini birleştirir; tekil (unicast) oyun
  trafiğini doğrudan etkilemesi beklenmez. Açıklama metni bunu söyler.
- **Maliyet:** güç. Aday olarak kalır ama beklentisi düşüktür; ölçüm karar verir.

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

### 4.3 Windows üzerinde doğrulanması gerekenler

Bu depoda Windows çalıştıran bir test ortamı yoktur; QoS davranışı **kod
tarafından varsayılmaz, ölçülür.** Bu mimari sayesinde platform beklendiği gibi
davranmazsa ilke kendiliğinden silinir ve hiçbir yanlış iddia üretilmez. Yine de
gerçek Windows 10/11 üzerinde şunlar doğrulanmalıdır:

1. `New-NetQosPolicy -AppPathNameMatchCondition <exe> -ThrottleRateActionBitsPerSecond N -PolicyStore ActiveStore`
   ilkesinin gerçekten gönderim hızını sınırlaması.
2. `Get-NetQosPolicy -PolicyStore ActiveStore` ile geri okunabilmesi.
3. `Remove-NetQosPolicy` sonrası sınırın kalkması.
4. Domain'e katılmış bir makinede GPO ilkesiyle birlikte davranış.

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
Kapasiteyi kullanıcı biliyorsa elle girebilir (`ManualUplinkMbps`).

### 4.5 WinDivert tabanlı shaper — **eklenmedi**

Windows QoS'un yetersiz kaldığı kanıtlanmadan kullanıcı alanında yeni bir paket
zamanlayıcı eklenmemiştir. Görev tanımındaki ship kriterleri (per-flow adalet,
ACK koruması, crash'te trafiğin kesilmemesi, ölçülmüş kazanç) gerçek Windows
entegrasyon testi olmadan karşılanamaz.

---

## 5. Gizlilik

- Profil dosyasında SSID, BSSID, IP adresi veya tam işlem yolu **tutulmaz**;
  ağ, erişim noktası ve hedef kısa hash'lerle temsil edilir.
- Günlüklerde genel IP, SSID, BSSID ve tam işlem yolu yazılmaz.
- Hiçbir ölçüm sonucu dışarı gönderilmez; tüm dosyalar
  `C:\ProgramData\DPI Bypass\` altında yereldir.

---

## 6. Fiziksel sınırlar

Aşağıdakiler bu yazılımla **düşürülemez** ve arayüz bunu böyle söyler:

- Sunucuya olan coğrafi mesafenin ışık hızı payı.
- ISP omurgasındaki ve peering noktalarındaki rota gecikmesi.
- Uzak sunucunun kendi tick/işleme gecikmesi.
- Karşı tarafın (indirme yönü) doldurduğu operatör kuyruğu — yerel bir hız
  sınırı oraya zamanında ulaşmaz.
- Oyunun kendi ağ modeli, interpolasyon ve tick rate'i.
