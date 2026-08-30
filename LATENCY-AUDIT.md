# Gecikme alt sistemi denetimi

Denetim tarihi: **2026-08-30**. İncelenen taban: `7035eb3`
("fix: restore Vodafone mode UI and preserve hotspot diagnostics").

Bu belge, "Ping düşürme" özelliğinin önceki hâlinde bulunan darboğazları, her
birinin kod üzerinde nasıl doğrulandığını ve ne yapıldığını kaydeder. Ölçüm
metodolojisinin dayandığı resmî kaynaklar için
**[LATENCY-RESEARCH.md](LATENCY-RESEARCH.md)**.

---

## Bulgular

| Kimlik | Önem | Durum | Bulgu ve kanıt | Yapılan |
| --- | --- | --- | --- | --- |
| LAT-001 | P1 | FIXED | **Aday kümesi neredeyse boştu.** `AdapterLatencyCapability.BuildSafeCandidates` yalnız `SelectiveSuspend`, `DeviceSleepOnDisconnect`, `D0PacketCoalescing` ve *yalnız Ethernet'te* `*InterruptModeration` üretiyordu; `WindowsLatencyAdapterController.DetectScript` de gelişmiş özelliklerden **sadece** `*InterruptModeration`'ı okuyordu. Bu dördünden yalnız biri belgelenmiş bir RTT mekanizmasına sahip. | `AdapterInterventionCatalog` eklendi: `*InterruptModeration`, `*RscIPv4`, `*RscIPv6`, `*RSS`, `*EEE`, `*LsoV2IPv4`, `*LsoV2IPv6` + iki güç özelliği. Her biri kapsam, risk, CPU/güç maliyeti, restart gereksinimi ve oturma süresi taşır. |
| LAT-002 | P1 | FIXED | **`DeviceSleepOnDisconnect` bir gecikme adayıydı.** Keyword, medya *bağlantısı kesildiğinde* ne olacağını yönetir (R4); bağlantı aktifken RTT ile ilgisi yok. | Aday listesinden çıkarıldı. Eski snapshot taşıyan makineler için `RestorablePowerProperties` içinde kaldı — yazılmaz, ama geri yüklenebilir. |
| LAT-003 | P1 | FIXED | **Yanlış hedef ölçülüyordu.** `LatencyProbe.RemoteEndpoints` sabit `1.1.1.1 / 8.8.8.8 / 9.9.9.9` idi; kullanıcının oyun sunucusuna giden rota hiç ölçülmüyordu. | `ILatencyTargetResolver`: genel referans (açıkça "oyun sunucusu değildir" etiketli), kullanıcı hedefi (`host[:port]`, `tcp://`, `udp://`) ve çalışan uygulamanın **gerçek uzak ucu** (IP Helper `GetExtendedTcpTable`). Hedef deney başında bir kez çözülür ve sabitlenir. |
| LAT-004 | P1 | FIXED | **Kuyruklanma yolu production'da hiç çalışmıyordu.** `LatencyPathAnalysis.Describe(measurement, loaded)` ikinci argümanı kabul ediyordu ama production çağrılarının hepsi tek argümanlıydı, yani `QueueingMs` **hiçbir zaman** dolmuyordu. README ve arayüz kuyruklanma tespitinden söz ederken kod bunu üretmiyordu. | `LoadedLatencyLane` + `ObservedLoadExperiment`: gerçek boşta/yük altında karşılaştırması, gönderim ve indirme ayrı raporlanır. `LoadedLatencyLaneTests` bu yolun production akışında çağrıldığını kanıtlar. |
| LAT-005 | P1 | FIXED | **Aktif yük testi yoktu ve eşik sabitti.** `NetworkLoadSampler` yalnız mevcut sayaçları okuyordu; `NetworkLoadSample.LoadedKbps = 256` her hat için aynıydı — 5 Mbit'lik bir gönderim hattını dolduran değer, 500 Mbit'likte gürültü. | Kullanıcının açık eylemiyle başlayan, sayaçlardan doğrulanan yük penceresi. `LinkCapacityEstimate` ile "yüklü" eşiği ölçülen kapasitenin payı (%25, mutlak taban korunur) ya da kullanıcının girdiği değer. Uygulama hiçbir bayt göndermez. |
| LAT-006 | P1 | FIXED | **Her tur A→B sırasındaydı.** `RunPairedCyclesAsync` önce baseline sonra candidate ölçüyordu; koşu boyunca kayan her şey tek kola yükleniyordu. | `PairedLatencyExperimentRunner` sırayı seed'li olarak turlar arasında değiştirir (ABBA). `LatencyEvaluationOptions.RequireBalancedOrder` production'da açıktır: tüm turlar aynı sıradaysa karar verilmez. |
| LAT-007 | P1 | FIXED | **Oturma süresi yoktu.** Değer yazıldıktan hemen sonra ölçülüyordu; ilk paketler durumu değil geçişi ölçer. | Her müdahale kendi `SettlingTime`'ını taşır; runner her apply ve her restore sonrası bekler. Probe artık ısınma örneklerini de atar. |
| LAT-008 | P1 | FIXED | **24 örnekten p99 iddiası.** `LatencyProbeRequest.Benchmark.ProbeCount = 24` iken p99 bir kabul metriğiydi; 100 yanıt şartı yalnız `ConfirmsMeaningfulImprovement` içindeydi, eşli evaluator'da yoktu. | Benchmark 40, derin geçiş 120 örnek. p99 iki kolda da ≥100 yanıt olmadan **karar metriği olamaz**; JSON çıktısında da `null` görünür. |
| LAT-009 | P1 | FIXED | **Tek final ölçüm.** Dakikalar önce alınan ilk baseline, tek bir son ölçümle karşılaştırılıyordu (`ConfirmsMeaningfulImprovement(baseline, final)`). | Kabul edilen paketin tamamı, yine dönüşümlü sırayla, özgün duruma karşı yeniden ölçülür (`ConfirmBundleAsync` + `LatencyComparison.ConfirmsBundle`). |
| LAT-010 | P2 | FIXED | **Yalnız ağ yükü karşılaştırılıyordu.** CPU/DPC yükü, güç kaynağı ve Wi-Fi sinyali/hızı hiç dikkate alınmıyordu. | `ILatencyEnvironmentSampler` (GetSystemTimes, GetSystemPowerStatus, WLAN sinyal/hız). Uyuşmayan çift atılır; rota/BSSID/adapter değişirse tur iptal edilir. |
| LAT-011 | P2 | FIXED | **Elemeler 30 gün boyunca körlemesine geçerliydi.** `LatencyProfile.MaximumAge` hem kabuller hem elemeler için aynıydı ve koşullar dikkate alınmıyordu. | Elemeler için 3 gün + `LatencyProfileContext` (hedef, güç kaynağı, erişim noktası, sinyal ve bağlantı hızı kovaları, yük altında ölçülüp ölçülmediği, QoS uygunluğu). Herhangi biri değişirse eleme geçersizdir. Arayüzde "Zorla yeniden ölç", CLI'da `latency retest`. |
| LAT-012 | P2 | FIXED | **"Kazanç yok" kapalı gibi görünüyordu.** UI yalnız `StatusLine` bağlıyordu; `NoGain` ile `Disabled` ayrımı kullanıcıya ulaşmıyordu. | `LatencyStatusView` + `LatencyModeState`: kapalı / ölçüyor / hızlı test / derin test / kazanç uygulandı / açık-kazanç yok / yalnız izleme / desteklenmeyen NIC / geri yükleme bekliyor / başarısız ayrı durumlardır ve ayrı cümlelerle gösterilir. |
| LAT-013 | P2 | FIXED | **Snapshot yalnız NIC özelliği taşıyordu.** Başka bir kaynak (QoS ilkesi) eklendiğinde crash sonrası temizlenecek bir kaydı olmayacaktı. | Şema 3: `Resources` listesi + `ILatencyResourceRestorer`. Geri alınamayan bir kaynak, geri alınabilenleri engellemez; kalan iş dosyada korunur. |
| LAT-014 | P2 | FIXED | **Yük altındaki gecikme için hiçbir müdahale yoktu.** Ev bağlantılarında en büyük kazanç buradadır. | `TrafficGuard`: Windows Policy-based QoS ile tek bir kullanıcı-seçimli uygulamanın giden trafiğine hız sınırı; ölçülen kuyruklanma azalmazsa, kayıp artarsa veya verim fazla düşerse ilke silinir. Yabancı ilkelere dokunulmaz. |
| LAT-015 | P3 | KAPSAM DIŞI | Çoklu adapter varsa `Set-NetIPInterface -InterfaceMetric` ile rota tercihi değiştirilebilir. | Yalnız **tanılama** yapıldı; otomatik yazma gerçek Windows doğrulaması olmadan gönderilmedi. Yanlış metrik bağlantıyı tamamen koparabilir. |
| LAT-016 | P3 | KAPSAM DIŞI | WinDivert tabanlı kullanıcı-alanı shaper. | Windows QoS'un yetersizliği kanıtlanmadan eklenmedi. Ship kriterleri (per-flow adalet, ACK koruması, crash'te trafiğin kesilmemesi, ölçülmüş kazanç) gerçek Windows entegrasyon testi gerektirir. |

---

## Yanlış pozitif kontrolleri

- **"Eşli A/B zaten vardı, sorun yoktu."** Vardı, ama tek yönlüydü (LAT-006),
  oturma süresi yoktu (LAT-007) ve son doğrulaması eşli değildi (LAT-009). Üçü
  birlikte, zamanla kayan bir hattın kayışını kazanç olarak raporlayabilirdi.
- **"Kuyruklanma zaten raporlanıyordu."** Kod yolu vardı ve testleri geçiyordu;
  production'da hiç çağrılmıyordu (LAT-004). Testin geçmesi özelliğin
  çalıştığını göstermiyordu.
- **"`NetworkLoadSample.LoadedKbps` yalnız bir eşik, zararsız."** Bu eşik hem
  "yüklü mü" sınıflandırmasını hem de çiftlerin karşılaştırılabilirliğini
  belirliyordu; yanlış sınıflandırılan bir pencere yanlış kabul üretebilirdi.
- **Checksum offload**'un hiçbir aday listesinde olmadığı bir birim testiyle
  sabitlendi; yasak liste hem C# kataloğunda hem PowerShell betiğinde uygulanır.

## Doğrulanamayanlar

Bu depoda Windows çalıştıran bir test ortamı yoktur. Aşağıdakiler **kod
tarafından varsayılmaz, ölçülür** — platform beklendiği gibi davranmazsa
değişiklik kendiliğinden geri alınır ve hiçbir iddia üretilmez — ama gerçek
Windows 10/11 üzerinde doğrulanmalıdır:

1. `New-NetQosPolicy … -ThrottleRateActionBitsPerSecond … -PolicyStore ActiveStore`
   ilkesinin gönderim hızını gerçekten sınırlaması ve `Remove-NetQosPolicy`
   sonrası sınırın kalkması.
2. `Get-NetAdapterAdvancedProperty -RegistryKeyword` ile `*RscIPv4`, `*RSS`,
   `*EEE`, `*LsoV2IPv4` yazma/geri okuma davranışı, ve `-NoRestart` ile yazılan
   değerin hangi sürücülerde canlı alınmadığı.
3. `Get-NetAdapterRsc` / `Get-NetAdapterRss` alan adlarının hedeflenen Windows
   sürümlerinde beklendiği gibi gelmesi.
4. `GetExtendedTcpTable` ile bulunan uzak ucun gerçek oyun sunucusu olması
   (TCP kullanan oyunlarda).
