# Gecikme denetimi V2 — bulgular, kanıtlar ve sınırlar

Bu belge, "Ping düşürme (Beta)" özelliğinin ikinci denetimidir. Her bulgu
**CONFIRMED**, **FIXED**, **NOT REPRODUCED**, **NOT RUN** veya **OUT OF SCOPE**
olarak işaretlidir ve kanıtı yanında yazılıdır.

`LATENCY-AUDIT.md` (V1) tarihsel kayıt olarak durmaktadır. V1'in "FIXED" dediği
maddeler bu turda **koda ve Microsoft belgelerine karşı yeniden doğrulandı**;
dördü gerçekten düzeltilmemişti ve aşağıda CONFIRMED olarak yazılıdır.

---

## 0. Baseline

Denetim, görev tanımındaki taban olan `main` üzerindeki **`e212141`**
commit'inden başladı. Depoda tag yoktur (`git tag` boş; `v1.0.0.73` sürüm
numarası CI çalışma numarasından üretilir ve GitHub Releases tarafında yaşar),
bu yüzden taban commit hash'i ile sabitlenmiştir. HEAD ilerlememişti — denetlenen
ağaç ile belirtilen taban aynıdır.

| | |
| --- | --- |
| Taban commit | `e212141` (`Merge pull request #12 …`) |
| Çalışma ağacı | temiz (`git status --short` boş) |
| Branch | `claude/dpi-bypass-latency-v2-ilcoue` |

> **Branch adı hakkında.** Görev tanımı `claude/real-latency-v2` istiyordu; bu
> oturuma atanan geliştirme dalı `claude/dpi-bypass-latency-v2-ilcoue` olduğu ve
> başka bir dala itmek açık izin gerektirdiği için çalışma atanan dalda
> yapılmıştır.

### 0.1 Ortam

Denetim ve derleme **Linux** üzerinde yapılmıştır. Bu, sonuçların ne anlama
geldiğini doğrudan sınırlar ve aşağıda her yerde belirtilmiştir.

| | |
| --- | --- |
| İşletim sistemi | Linux 6.18.44 (x86_64) — konteyner |
| .NET SDK | 10.0.400 |
| PowerShell | 7.4.6 (Linux) |
| Windows | **yok** |
| NIC / sürücü | **yok** |

**Sonuç:** bu depodaki hiçbir doğrulama gerçek bir Windows NIC'i, gerçek bir
sürücü, gerçek bir QoS ilkesi veya gerçek bir hat doygunluğu görmemiştir. Birim
testleri karar mantığını test çiftleri üzerinde kanıtlar; **Windows davranışını
kanıtlamaz.** Bkz. §5.

### 0.2 Baseline komut sonuçları (değişiklik yapılmadan önce)

| Komut | Sonuç |
| --- | --- |
| `dotnet restore` | ✅ başarılı (`-p:EnableWindowsTargeting=true` ile; WPF hedefi Linux'ta bunu gerektirir) |
| `dotnet build DpiBypass.slnx -c Release` | ✅ başarılı, 0 uyarı |
| `dotnet test tests/DpiBypass.Tests -c Release` | ✅ **654 geçti**, 0 başarısız |
| `pwsh -File scripts/tests/install.tests.ps1` | ✅ tüm testler geçti |
| `pwsh -File scripts/tests/xaml-resources.tests.ps1` | ✅ 88 anahtar, 341 referans |
| `dotnet format --verify-no-changes` | ❌ **94 whitespace hatası — hepsi `src/DpiBypass.Core/Ipc/ControlCommands.cs` içinde** |

Son satır **taban durumdur ve bu çalışmayla ilgisizdir**: aynı komut `e212141`
üzerinde ayrı bir worktree'de çalıştırıldığında aynı 94 hatayı aynı dosyada
verir. Bu denetimde dokunulan her `.cs` dosyası `dotnet format` ile
biçimlendirilmiştir; ilgisiz bir dosyayı bu değişikliğin içinde yeniden
biçimlendirmemek için `ControlCommands.cs` olduğu gibi bırakılmıştır.

---

## 1. P0 bulguları

### P0.1 — NIC ayarı gerçekte uygulanmıyordu · **CONFIRMED → FIXED**

**Kanıt (kod).** `WindowsLatencyAdapterController.ApplyScript`, değeri
`Set-NetAdapterAdvancedProperty … -NoRestart` ile yazıyor, aynı anahtarı
registry'den geri okuyor ve eşitse `Applied = true` döndürüyordu. `ApplyDto`
yalnız `(bool Applied, string? Reason)` taşıyordu.

**Kanıt (Microsoft, R15, 31 Ağustos 2026'da yeniden açıldı).**
`Set-NetAdapterAdvancedProperty` referansı, `-NoRestart` için:

> *"Indicates that the cmdlet does not restart the network adapter after
> completing the operation. **Many advanced properties require restarting the
> network adapter before the new settings take effect.**"*

Yani registry'nin yeni değeri göstermesi, sürücünün o değerle **çalıştığını**
kanıtlamaz. Bu haliyle A/B deneyi eski davranışı iki kez ölçüp farkı yeni değere
yazabiliyordu.

**Düzeltme.** `LatencyApplyResult` yedi durumlu bir sonuca dönüştü:
`Refused`, `RegistryWritten`, `RestartRequired`, `AdapterRestarted`,
`OperationallyVerified`, `NotVerified`, `LinkNotRestored`, `RolledBack`.
Ölçüme yalnız `IsEffective` (yani `OperationallyVerified` veya
`AdapterRestarted`) izin verir. Zincir:

1. `-NoRestart` ile yaz → registry geri okuması **eşleşmezse** `Refused`.
2. Operational sorgusu olan anahtarlarda (`*RscIPv4/6`, `*RSS`, `*LsoV2IPv4/6`)
   `Get-NetAdapterRsc` / `Get-NetAdapterRss` / `Get-NetAdapterLso` sorulur.
   İstenen durumu bildiriyorsa `OperationallyVerified` — yeniden başlatma yok.
3. Operational sorgusu **olmayan** anahtarlarda (`*InterruptModeration`, `*EEE`)
   onay yoksa `RestartRequired` ve **ölçüm yapılmaz**.
4. Onay varsa `Restart-NetAdapter`, sonra aynı GUID + link up + IPv4 adresi +
   varsayılan rota beklenir; gelmezse `LinkNotRestored` ve geri alma.
5. Yeniden başlatma sonrası C# tarafında arabirim indeksi, ilk atlama hash'i,
   erişim noktası hash'i ve hedefe erişim doğrulanır
   (`LatencyOptimizer.DescribePostRestartProblemAsync`); biri değişmişse tur
   iptal edilir ve değer geri alınır.

**Uzak oturum.** `SessionKind.IsRemoteSession()` (`SM_REMOTESESSION`, R23) doğru
dönerse yeniden başlatma **hiçbir koşulda** yapılmaz — onay verilmiş olsa bile.

**Snapshot sırası.** Değişmedi ve doğruydu: özgün değer diske atomik olarak
yazılmadan adaptöre dokunulmaz (`LatencySnapshotStore.SaveAsync` → tmp dosya,
`Flush(flushToDisk: true)`, `File.Replace`).

**Testler.** `ARegistryWriteTheDriverHasNotPickedUpIsNeverMeasuredOrKept`,
`OnlyAnEffectiveApplyMayBeMeasured` (8 durum),
`AnAdapterThatDoesNotComeBackEndsWithNothingApplied`,
`ARemoteSessionRefusesAdapterRestartsEvenWithConsent`,
`OnlyKeywordsWindowsCanReportOnAreEverCalledOperationallyVerified`.

**Sınır — NOT RUN.** Gerçek bir sürücünün `-NoRestart` sonrası hangi anahtarı
canlı aldığı **ölçülmemiştir**. Kod bunu varsaymıyor, soruyor; ama sorunun
cevabı bu depoda yok.

---

### P0.1b — `SelectiveSuspend` ve `D0PacketCoalescing` · **CONFIRMED → FIXED**

**Kanıt.** V1 her ikisini de steady-state RTT deneyiyle ölçüyordu.
`SelectiveSuspend` (R5) yalnız **uzun boşluktan sonraki ilk paketi** etkiler ve
sürekli probe gönderen bir deney o durumu hiç üretmez. `D0PacketCoalescing` (R8)
**yayın/çoklu yayın** alımlarını birleştirir; unicast oyun trafiğine doğrudan
mekanizması yoktur.

**Düzeltme.** `AdapterInterventionCatalog.WritablePowerProperties` **boş**.
Descriptor'lara `AffectsSteadyStateRtt` eklendi ve ikisi için `false`; `Build`
bu bayrağı taşımayan hiçbir adayı üretmez. `RestorablePowerProperties` üçünü de
(bunlar + `DeviceSleepOnDisconnect`) taşımaya devam eder, böylece eski bir
snapshot hâlâ geri yüklenir. Ayrı bir first-packet deneyi **yazılmadı**; görev
tanımı "geliştir **veya** varsayılan optimizasyondan çıkar" diyordu ve çıkarmak,
ölçülmemiş bir ayarı optimize edilmiş gibi göstermeme kuralıyla uyumludur.

**Test.** `ThePowerKeywordsAsteadyStateExperimentCannotSeeAreNoLongerOffered`.

---

### P0.2 — "Yük altında" gerçek doygunluk değildi · **CONFIRMED → FIXED**

**Kanıt (kod).** `LinkCapacityEstimate.LoadedShare = 0.25` ve bilinmeyen
kapasitede 256 kbit/s sabit eşiği. Dahası `Observing()` kapasiteyi **tek
pencereden** öğreniyordu: `UplinkKbps = Math.Max(UplinkKbps ?? 0, sample.UplinkKbps)`.
Bu ikisi birlikte kendini doğrulayan bir döngü kurar — sayaçları yalnız 2 Mbit/s
görmüş bir makine 500 kbit/s'i doygunluk sayar, olmayan kuyruklanmayı ölçer ve
bir dahaki sefere kendi hatasını teyit eder.

**Düzeltme.**

- **Üç ayrı durum**: `LinkLoadClassification` = `Quiet` / `Traffic` /
  `HighUtilisation` / `Saturated` / `Unknown`. Yalnız `Saturated` bir
  kuyruklanma iddiasını destekler (`LoadExperimentResult.ProvesQueueing`).
- **Rampalı estimator**: `LinkCapacityRamp` art arda pencereleri toplar; son üç
  pencere birbirine ≤%15 içinde **ve** tepe değerin ≥%90'ında ise plato kabul
  eder ve medyanını kapasite olarak verir (`Measured`). Değilse tepe değer
  yalnız bir **alt sınırdır** (`Weak`). Aktarımdaki bir boşluk rampayı
  sıfırlar — duraklamanın iki yanındaki pencereler ardışık değildir.
- **Doygunluk = ölçülmüş kapasitenin %85'i** (`SaturationShare`), %60 üstü
  `HighUtilisation` olarak ayrıca raporlanır. **%25 kuralı kaldırıldı.**
- **Güven düşükse "ölçülemedi"**: `IsConfident` yalnız `Measured` veya
  `UserSupplied` için doğrudur; kapasitesi bilinmeyen bir hat asla `Saturated`
  sınıflandırılamaz. Traffic Guard bu durumda "kuyruklanma yok" demez,
  *"hat doygunluğa ulaşmadı; bu, kuyruklanma yok demek değildir"* der.
- **Yön başına saklama**: `UplinkKbps`/`DownlinkKbps`, `UplinkConfidence`/
  `DownlinkConfidence`, `UplinkObservedAt`/`DownlinkObservedAt`, pencere
  sayıları. Kullanıcının girdiği değer bir gözlemle **asla** ezilmez.
- **Metered/mobil**: otomatik yük sağlayıcısı **hiç eklenmedi** (§3.2), yani
  kapatılacak otomatik trafik de yok. Bu, bir bayrak koymaktan daha güçlü bir
  garantidir.

**Testler.** `AQuarterOfTheLinkIsNotSaturation`,
`SaturationIsNeverClaimedWithoutAConfidentCapacity`,
`CapacityIsLearnedFromAPlateauNotFromOneWindow`, `AGapInTheTransferRestartsTheRamp`,
`UploadAndDownloadCapacitiesAreKeptApart`,
`AUserSuppliedCapacityOutranksWhatWeHappenedToSee`,
`AnUnsaturatedBaselineProducesNotMeasuredRatherThanNoQueueing`.

---

### P0.3 — Derin test production'da tamamlanamıyordu · **CONFIRMED → FIXED**

**Kanıt (kod).** `LoadedLatencyLane.RunAsync` sırasıyla bir upload, bir download
ve Traffic Guard için **iki upload daha** bekliyordu (guard'ın before/after
turları). Arayüzdeki `MainViewModel.LatencyLoadInstruction` ise sabit tek bir
satırdı: `_service.LatencyLoadInstruction(LoadDirection.Upload)`. Yani kullanıcı
tek bir upload isteği görüyor, uygulama sessizce üç aktarım daha bekliyordu.
Kaynağı okumayan biri bu testi bitiremezdi.

**Düzeltme.** Çalıştırma açık bir state machine oldu (`LoadedLaneStage`, 18
durum) ve her aşama girildiği anda karta yayımlanıyor
(`ILatencyStageReporter` → `ProtectionService.LatencyStageChanged` →
`MainViewModel.ApplyLatencyStage` → XAML).

Görev tanımındaki 12 adımın karşılıkları:

| İstenen | Durum |
| --- | --- |
| Hedef doğrulama | `VerifyingTarget` |
| Hattın boşalmasını bekleme | `WaitingForQuietLink` |
| Idle baseline | `IdleBaseline` |
| Upload baseline için aktarım isteme | `AwaitingUploadStart` → `MeasuringUploadBaseline` |
| Upload'u durdurma + boşalma | `AwaitingUploadStop` |
| Aday/policy uygulama | `ApplyingPolicy` |
| Policy sonrası **yeni** upload isteme | `AwaitingFreshUpload` |
| Candidate ölçümü | `MeasuringUploadCandidate` |
| Upload'u durdurma | `AwaitingUploadStop` (tur başına tekrar) |
| Download aşaması ayrı | `AwaitingDownloadStart` → `MeasuringDownload` |
| Out-of-sample confirmation | `Confirming` |
| Commit / rollback | `Committed` / `RolledBack` / `NoGain` / `Cancelled` / `Failed` |

Her aşamada kart şunları gösterir (`LoadedLaneProgress`): ne beklendiği, yön,
hedef, sayaçtan okunan anlık hız, kapasiteye yaklaşma yüzdesi, kalan süre,
kullanılan veri, iptal düğmesi ve aşamanın sonuç nedeni.

**Testler.** `TheWizardAsksForEveryTransferItActuallyNeeds` (sıralama dahil:
durdurma → policy → yeni aktarım), `EveryStageHasATitle`,
`TheStagesThatNeedTheUserAreTheOnesThatSayTheyDo`,
`TheRateLineShowsHowCloseToCapacityTheTransferIs`,
`TheCardShowsTheStagePanelTheCancelButtonAndTheResultBlock`.

---

### P0.4 — QoS ilkesi mevcut bağlantıya uygulanmıyordu · **CONFIRMED → FIXED**

**Kanıt (kod).** `TrafficGuard.RunAsync` önce `_load.RunAsync` ile baseline
ölçüyor, **sonra** ilkeyi oluşturuyor, sonra tekrar ölçüyordu. Arada aktarımın
durdurulup yeniden başlatılması istenmiyordu.

**Kanıt (Microsoft, R21).** QoS Policy Architecture: QoS Inspection Module
*"waits for indications of QoS policy changes …, retrieves the QoS policy
settings, and interacts with the Transport Layer and Pacer.sys to internally
mark traffic that matches the QoS policies."* Eşleşme transport uç noktası
oluşturulurken yapılır; hâlihazırda açık bir aktarım yeni ilkeye tabi olmaz.
Yani V1 sınırsız akışı ölçüp farkı ilkeye yazabiliyordu.

**Kanıt (kod, ikinci hata).** `CreateScript` geri okumayı
`Get-NetQosPolicy | Where-Object { $_.Name -eq $name }` ile yapıyordu — yalnız
**ad**. Throttle oranı, uygulama eşleşmesi, öncelik ve depo doğrulanmıyordu.

**Düzeltme.**

- **Sıralama**: aktarımı durdur → hattın boşalmasını bekle → ilkeyi oluştur →
  **yeni akış** bekle → ölç. Yeni akış WinDivert FLOW katmanından doğrulanır
  (süreç kimliği + `EstablishedAt >= ilke oluşturma anı`). Gelmezse
  `TrafficGuardStatus.NeedsNewConnection` ve **sonuç üretilmez**.
- **Gözlem yoksa sonuç yok**: akış gözlemcisi çalışmıyorsa gereksinim
  kanıtlanamaz, o yüzden ölçüm de yapılmaz.
- **Tam read-back**: `WindowsQosController.DescribeMismatch` ad, depo,
  `AppPathName`, `IPProtocol`, `IPDstPrefix`, `IPDstPort`, `ThrottleRateAction`,
  `DSCPValue`, `Precedence` ve ad alanı sahipliğini tek tek karşılaştırır;
  biri tutmuyorsa ilke "oluşturulmadı" sayılır.
- **Etkili mi**: ölçülen bayt hızı sınırın %110'unu aşıyorsa
  (`TrafficGuardCapPlanner.RateHonoured`) o tur, o sınır hakkında **veri
  sayılmaz** — ilke deposunda görünüyor olsa bile.
- **Doğrulanmış process picker**: `BulkApplicationSelection` süreç kimliklerini,
  görüntü adını ve okunabiliyorsa **doğrulanmış tam yolu** ayrı tutar; ilke
  yolu varsa yolu, yoksa görüntü adını eşleştirir ve farkı kullanıcıya yazar.
  Çalışmayan bir uygulama `ApplicationNotRunning` ile reddedilir.
- **Kimin sınırlandığı**: kart ve özet, sınırlananın **oyun değil toplu aktarım
  yapan uygulama** olduğunu açıkça söyler.
- **Yabancı ilkeler**: değişmedi ve doğruydu — `DPIBypass.Latency.` dışındaki
  hiçbir ad oluşturulamaz, silinemez; rakip bir ilke varsa geri çekilinir.

**Testler.** `WithoutANewFlowAfterThePolicyNoResultIsProduced`,
`WithoutAFlowObserverTheGuardRefusesToProduceAVerdict`,
`ACapTheTrafficIgnoredIsNotTreatedAsAMeasurementOfThatCap`,
`APolicyThatDiffersInAnyFieldIsNotAccepted` (8 alan),
`APolicyCarryingAForeignNameIsRefusedEvenIfEverythingElseMatches`,
`AnApplicationThatIsNotRunningIsRefusedRatherThanGuessedAt`,
`ThePacedApplicationIsTheBulkOneAndIsReportedAsSuch`.

**Sınır — NOT RUN.** Throttle'ın **tek ve çoklu TCP akışında** uygulama
toplamına mı akış başına mı davrandığı gerçek Windows'ta ölçülmemiştir. R10 ve
R21 bunu söylemiyor. Kod bu soruyu varsaymıyor: ölçülen toplam hız sınırla
tutarlı değilse özellik başarılı sayılmaz. Harness bu soruyu kendi raporunda
`notRun` olarak yazar.

---

### P0.5 — Sabit %85 throttle · **CONFIRMED → FIXED**

**Kanıt (kod).** `TrafficGuardRequest.ThrottleShare = 0.85`, tek değer, hiç
ölçülmüyordu.

**Düzeltme.** `TrafficGuardCapPlanner`:

- Kapasite ölçüldükten sonra mod başına aday oranlar üretilir — *Dengeli*:
  0.92 / 0.80 / 0.68, *En düşük gecikme*: 0.80 / 0.65 / 0.50. **Azalan sırada**,
  yani en az müdahale eden cap önce denenir.
- Her cap uygulanır ve **gerçekten ölçülür** (uygulama, yeni akış, ölçüm).
- Sıralama önceliği görev tanımındaki gibi: **yük altı p95 → p99 → kuyruklanma
  farkı → jitter → kayıp → korunan throughput**.
- *Dengeli* mod, p95'i en iyiden ≤3 ms (veya %10) uzakta olan adaylar arasından
  **en yüksek throughput'u** seçer: bir cap 1 ms daha iyi olup aktarımı yarıya
  indiriyorsa daha iyi bir cap değildir.
- *En düşük gecikme* modu tabanı %40'a indirir ve **ölçülen hız kaybını**
  sonuç ekranında gösterir.
- Seçilen cap **aramada kullanılmayan ayrı bir doğrulama turunda** yeniden
  ölçülür; tekrarlamazsa ilke silinir.
- Kabul için ayrıca: kuyruklanma ≥10 ms **ve** ≥%25 azalmalı, kayıp artmamalı,
  throughput moda göre taban üstünde kalmalı.
- Ağ/adapter/sürücü/hedef değişince eski sonuç geçersizdir:
  `AdapterLatencyCapability.CapabilityFingerprint` artık **sürücü sürümünü** de
  içerir, `LatencyTargetSpec.CacheKey` sabitlenen uç noktayı da içerir.

**Testler.** `TheCapIsChosenByMeasurementAndConfirmedSeparately` (4 tur:
baseline + 2 arama + 1 doğrulama; seçilen cap'in 0.85 olmadığı da doğrulanır),
`BalancedModeTradesTheLastMillisecondForThroughput`,
`LowestLatencyModeTakesTheBestTail`, `TheSearchStartsFromTheLeastDisruptiveCap`.

---

### P0.6 — UDP oyun hedefi bulunamıyordu · **CONFIRMED → FIXED**

**Kanıt (kod).** `LatencyTargetResolver.ResolveApplication` yalnız TCP tablosuna
bakıyor ve **en çok bağlantısı olan** adresi seçiyordu:
`.GroupBy(endpoint => endpoint.Address).OrderByDescending(group => group.Count())`.
UDP için cevabı *"sunucu adresini 'Özel hedef' alanına yazın"* idi.

**Düzeltme.**

- **WinDivert FLOW katmanı** (`WinDivertFlowObserver`, R22): `SNIFF | RECV_ONLY`
  ile açılır — belgenin bu katman için **zorunlu** kıldığı bayraklar, ve engelleme
  / değiştirme / enjeksiyon yeteneği olmayan bir handle. TCP ve UDP için süreç
  kimliği ve tam beşli gelir.
- **Sıralama** artık bağlantı sayısı değil, bir oturumun nasıl davrandığıdır
  (`GameEndpointDiscovery`): akış hâlâ açık mı, ne kadar sürdü, UDP mi, uzak
  port ephemeral aralığın altında mı — ve ancak en sonda aynı adrese kaç akış
  var.
- **Kullanıcı seçimi**: birden fazla aday varsa hepsi puanı ve **nedeniyle**
  karta düşer; kullanıcı birini sabitler (`PinnedEndpoint`) ve o tur boyunca
  değişmez.
- **Belgelenmiş kısıt dürüstçe gösterilir**: R22, *"the WINDIVERT_LAYER_FLOW
  layer cannot capture flow events that occurred before the handle was opened."*
  Gözlem açıldıktan sonra akış görülmediyse kullanıcıya **oyuna yeniden
  bağlanması** söylenir.
- **Gizlilik**: akışlar yalnız bellekte, yalnız bir tur boyunca. Diske veya
  günlüğe IP/süreç yolu yazılmaz.
- **Sahte oyun ping'i yok**: UDP uç noktası ICMP ile ölçülür ve
  `RouteReferenceOnly = true` ile "aynı adrese rota referansı" olarak
  etiketlenir; sonuç ekranı bunu her seferinde yazar.
- **Minecraft Java**: Server List Ping'in Ping/Pong çifti zamanlanır
  (`MinecraftStatusProbe`) — bu, oyunun kendi listesinde gördüğü sayıdır. Tek
  bağlantı üzerinden ölçülür. **Genelleştirilmedi.**

**Testler.** `AUdpGameServerIsDiscoveredFromTheObservedFlows`,
`AUdpOnlyApplicationWithNoObservedFlowsIsToldWhatToDo`,
`AnOpenSessionOutranksTheBusiestAddress`, `APinnedEndpointIsHonouredOverTheRanking`,
`TheFlowObserverOnlyEverListens` (satır satır: yalnız FLOW katmanı, yalnız
`Sniff | RecvOnly`, `Send` yok, `Network` katmanı yok).

---

### P0.7 — Probe motoru · **CONFIRMED → FIXED**

| Alt bulgu | Durum | Kanıt ve düzeltme |
| --- | --- | --- |
| TCP probe'larında `TimeoutMilliseconds` kullanılmıyordu | **CONFIRMED → FIXED** | `TryTcpConnectAsync` yalnız çağıranın token'ını alıyordu; kara deliğe giden bir SYN işletim sisteminin yeniden iletim çizelgesinde ~20 sn oturuyordu — tüm serinin süresinden uzun. Artık her connect kendi `CancelAfter` deadline'ıyla. Test: `ATcpProbeNeverOutlastsItsOwnDeadline`. |
| Gateway ve uzak pencereler hizalı değildi | **CONFIRMED → FIXED** | `GatewayPacing` pencereyi `Pacing × ProbeCount` varsayıyordu; TCP serisi (≥120 ms tempo, connect süreleri) çok daha uzun sürdüğü için gateway örnekleri pencerenin yalnız başını kaplıyor, sonra tüm pencerenin medyanından çıkarılıyordu. Artık gateway serisi **uzak seri bitene kadar** sürer ve gerçekten yaptığı deneme sayısını raporlar. |
| ICMP çözünürlüğü altındaki kazançlar | **CONFIRMED → FIXED** | `Ping.RoundtripTime` tam milisaniye verir; `MedianGainFloorMs = 0.8` bunun altındaydı. `LatencyMeasurement.ClockResolutionMs` eklendi (ICMP 1.0, stopwatch ~1/`Stopwatch.Frequency`) ve `LatencyComparison` her eşiği çözünürlüğe yükseltir. Test: `AGainSmallerThanTheClockResolutionIsRefused` + karşıtı. |
| Sürekli yeni TCP el sıkışması | **CONFIRMED → FIXED** | Var olan bağlantının RTT'si IP Helper EStats ile okunur (R19/R20): hiç paket gönderilmez. `EnableCollection` doğru değilse **hiç örnek üretilmez**. Minecraft'ta tek bağlantı üzerinden ölçülür. |
| TCP SYN süresi oyun RTT'si gibi gösteriliyordu | **CONFIRMED → FIXED** | Protokol etiketleri artık `TCP/443 (el sıkışma süresi)`, `TCP/25565 (EStats)`, `Minecraft/25565`; `LatencyEndpoint.MeasuresApplicationRoundTrip` ayrımı yapar ve sonuç ekranı her seferinde hangisi olduğunu yazar. |
| ICMP/TCP/uygulama sonuçlarının karışması | **NOT REPRODUCED** | V1 de karıştırmıyordu: `LatencyPair.HasSameMeasurementPath` protokolü ve portu karşılaştırır. V2 bunu korudu ve etiketleri ayrıştırdı. |
| ABBA sırası, paired bootstrap, context doğrulaması, p99 için ≥100 cevap | **NOT REPRODUCED (regresyon yok)** | `PairedLatencyExperimentRunner.OrderFor`, `LatencyEvaluationOptions.Strict` (`RequireBalancedOrder`, `RequireConfidenceInterval`), `MinimumRepliesForP99 = 100` korundu. Test: `AnAcceptedGainAlwaysCarriesAnIntervalThatExcludesZero`. |
| Güven aralığı sıfırı içerdiğinde kazanç denmemesi | **NOT REPRODUCED (korundu)** | Aynı test. |
| Pratik anlamlılık (effect size) | **NOT REPRODUCED (korundu ve güçlendirildi)** | Eşikler duruyor, üstüne çözünürlük tabanı eklendi. |

---

## 2. Müdahale sırası

Görev tanımındaki sıra uygulanmıştır:

1. **Gerçek hedef ve güvenilir ölçüm** — P0.6, P0.7. ✅
2. **Gerçek hat doygunluğu** — P0.2. ✅
3. **NIC ayarının operasyonel etkinleşmesi** — P0.1. ✅
4. **Adaptif Windows QoS Traffic Guard** — P0.4, P0.5. ✅
5. **Çoklu fiziksel bağlantı karşılaştırması** — **OUT OF SCOPE.** Yalnız
   kullanıcı onayıyla ve aynı hedefe bağlanarak yapılabilir; bu turda
   eklenmemiştir çünkü 1–4 arası kalemlerin hiçbiri gerçek Windows'ta
   doğrulanmamıştır ve doğrulanmamış bir temelin üstüne yenisini koymak,
   düzeltilen hataların türünü tekrarlamaktır.
6. **Deneysel shaper** — **OUT OF SCOPE**, §3.3.

---

## 3. Eklenmeyenler ve nedenleri

### 3.1 Otomatik yük sağlayıcısı — **OUT OF SCOPE**

`AutomaticLoadProvider` **ship edilmemiştir.** Görev tanımı bunu "yalnız resmî,
kararlı, belgelenmiş ve gizlilik şartları uygun bir test endpoint'i bulunursa"
diye koşullamıştı. Böyle bir uç nokta doğrulanamadı: M-Lab/NDT7 verisi kamuya
açıktır ve bilgilendirilmiş onam gerektirir (bkz. LATENCY-RESEARCH §4.4);
Cloudflare'in hız testi uç noktaları bu amaç için belgelenmiş bir sözleşme
sunmuyor. Belgesiz bir uç noktayı sessizce hardcode etmek görev tanımının açık
yasağıdır.

**Bunun yerine** manuel sihirbaz tamamlanmıştır (P0.3): kullanıcı zaten
yapacağı aktarımı başlatır, uygulama **hiçbir bayt göndermez**, ve her aşamada
ne beklendiği yazılıdır. Bunun yan etkisi olarak metered/mobil bağlantı için
kapatılacak bir otomatik trafik de yoktur.

### 3.2 First-packet deneyi — **OUT OF SCOPE**

Bkz. P0.1b. Görev tanımı "geliştir **veya** çıkar" diyordu; çıkarıldı.

### 3.3 WinDivert tabanlı packet shaper — **OUT OF SCOPE**

Ship kriterleri (yalnız bulk akışları yakalamak, oyun/ses/DNS/ACK'i
geciktirmemek, bounded queue, monotonic clock, batch I/O, düşük CPU, crash'te
fail-open, mevcut DPI handle öncelikleriyle çakışmamak, IPv4/IPv6 + TCP/UDP +
fragmentation güvenliği, çoklu akış adaleti, **ölçülmüş** p95/jitter kazancı,
kaldırıldığında tam normale dönüş) gerçek Windows entegrasyon testi olmadan
karşılanamaz. Dahası ön koşul —"önce Windows QoS'un neden yetersiz olduğunu
gerçek testlerle kanıtla"— sağlanmamıştır: QoS'un yetersizliği **ölçülmemiştir**.

Kötü yazılmış bir user-space paket yolu gecikmeyi artırır. Eklenmemiştir.

### 3.4 Interface metric / çoklu adapter — **OUT OF SCOPE**

V1'de olduğu gibi yalnız tanılama; hiçbir metrik değiştirilmez.

---

## 4. İndirme yönünde dürüstlük

Yerel bir gönderim sınırı, indirme sırasında **operatörün ekipmanında** oluşan
kuyruğa ulaşamaz: paketler oraya varmadan önce sıraya girer, ve buradan verilen
bir hız sınırı karşı tarafın gönderim hızını değiştirmez.

Uygulanan davranış:

- İndirme yönünde **yalnız ölçülen** sonuç gösterilir; hiçbir müdahale yapılmaz.
- Ölçülen indirme kuyruklanması eşiği aşarsa sonuç bir **tanı** olarak yazılır:
  *"Bu kuyruk operatörün ekipmanında oluşur; bu bilgisayarda uygulanan bir
  gönderim sınırı ona ulaşamaz. Kalıcı çözüm yönlendiricide SQM/CAKE veya
  FQ-CoDel gibi bir kuyruk yönetimidir."*
- "Download latency fixed" gibi bir ifade hiçbir yerde üretilmez.

**Test.** `TheCardIsHonestAboutWhatALocalLimitCannotReach`.

---

## 5. Gerçek Windows entegrasyonu — **NOT RUN**

Bu, bu belgedeki en önemli satırdır.

**Bu sürümde hiçbir gerçek Windows ölçümü yapılmamıştır.** Denetim ve derleme
Linux üzerinde yapılmıştır (§0.1). Aşağıdakilerin hiçbiri doğrulanmamıştır:

- Bir sürücünün `-NoRestart` sonrası hangi anahtarı canlı aldığı.
- `Get-NetAdapterRsc/Rss/Lso` operational değerlerinin gerçek raporlaması.
- Kontrollü `Restart-NetAdapter` sonrası bağlantının geri gelme davranışı.
- QoS throttle'ın gerçekten hız sınırlayıp sınırlamadığı.
- Throttle'ın tek/çoklu akışta uygulama toplamına mı davrandığı.
- İlke öncesi açılmış bir bağlantının ilkeye tabi olup olmadığı.
- WinDivert FLOW katmanının bu makinede açılıp açılmadığı.
- TCP EStats'ın gerçek bir oyun bağlantısında değer üretip üretmediği.
- Gerçek bir hattın plato yapıp yapmadığı ve hangi hızda.
- HP EliteBook 840 G3 sınıfı 2C/4T Skylake üzerinde idle CPU ve RAM.

**Birim testlerinin geçmiş olması bunların hiçbirini kanıtlamaz.** 716 test,
karar mantığının test çiftleri üzerinde doğru olduğunu gösterir — Windows'un
beklendiği gibi davrandığını değil.

### 5.1 Harness

`scripts/integration/latency-windows.ps1` bu boşluğu kapatmak için eklenmiştir
ve **birim testlerinden ayrıdır; onlarla birlikte çalışmaz.** Kaydettikleri:

- Windows sürümü/build, PowerShell sürümü, uzak oturum olup olmadığı, çekirdek
  sayısı.
- NIC adı, açıklaması, GUID, **sürücü sürümü ve tarihi**, link hızı, medya türü.
- Tüm gelişmiş özellikler: anahtar, mevcut değer, geçerli değerler.
- **Başlangıç / aday / geri yükleme** operational durumları (RSC, RSS, LSO, link,
  IPv4 adresi, varsayılan rota).
- Idle ölçümü: median / p95 / p99 (yalnız ≥100 cevapta) / jitter / kayıp ve
  **saat çözünürlüğü**.
- QoS: yabancı ilke sayısı ve adları, oluşturulan ilkenin **tam alan** geri
  okuması, kaldırılıp kaldırılmadığı, **yabancı ilkelere dokunulmadığının**
  doğrulaması.
- Kullanılan veri (gönderilen/alınan bayt).
- Geri yüklemenin özgün değerle eşleşip eşleşmediği.
- Ve **kuramadığı her şey**: rapor içinde `notRun` dizisi.

Güvenlik: her yazma ayrı bir switch'in arkasındadır (`-AllowWrite`,
`-AllowRestart`, `-AllowQos`), yeniden başlatma uzak oturumda reddedilir,
oluşturulan tek ilke `DPIBypass.Latency.lab.harness` adıyla ve `ActiveStore`
içindedir, `Remove-NetQosPolicy` yalnız o adı alır. Bu kurallar CI'da
`scripts/tests/latency-harness.tests.ps1` ile satır satır doğrulanır.

`artifacts/latency-lab/*.json` çıktıları `.gitignore` kapsamındadır; bir
ölçüm yapıldığında raporu bu belgeye eklenmelidir.

---

## 6. Güvenlik ve kurtarma

| Gereksinim | Durum | Nerede |
| --- | --- | --- |
| Snapshot her yazmadan önce atomik | ✅ (V1'den korundu) | `LatencySnapshotStore.SaveAsync`: tmp → `Flush(flushToDisk)` → `File.Replace` |
| NIC + QoS + yeni kaynak türleri aynı transactional modelde | ✅ | `LatencyOptimizationSnapshot.Resources`, `LatencySnapshotRestorer` |
| Cancel/exception/shutdown'da `finally` ile geri alma | ✅ | `TrafficGuard.RunAsync` `catch`/`RemoveSafelyAsync`, `LoadedLatencyLane` iptal yolu, `LatencyOptimizer.TryRestoreAfterFailureAsync`, `DisposeAsync` |
| Crash sonrası başlangıç kurtarması idempotent | ✅ | `LatencyOptimizer.RecoverAsync` — gate altında snapshot yeniden okunur |
| Başka programların ayarları ve QoS ilkeleri korunur | ✅ | `WindowsQosController.IsOwnedName` iki tarafta (C# + script) |
| Sürücü güncellemesi sonrası eski profil geçersiz | ✅ **güçlendirildi** | `CapabilityFingerprint` artık `DriverVersion` içerir |
| Adapter/ağ/BSSID/rota değişirse iptal | ✅ **genişletildi** | `PairedLatencyExperimentRunner.MovedNetwork` + yeni `DescribePostRestartProblemAsync` |
| DPI motoru ve DNS proxy bozulmaz | ✅ | `DpiFastPathTests` genişletildi: FLOW gözlemcisi satır satır sınırlandı |
| Idle CPU / RAM | **NOT RUN** | Ölçüm için Windows gerekir. Tasarım tarafı: FLOW handle yalnız derin test sürerken açık, olay hızı bağlantı başına birkaç olay; probe'lar sıralı ve tempolu. |

---

## 7. Doğrulama sonuçları

`e212141` tabanına göre, bu daldaki son durum:

| Komut | Sonuç |
| --- | --- |
| `dotnet restore` | ✅ |
| `dotnet build DpiBypass.slnx -c Release` | ✅ 0 uyarı, 0 hata (Core + App + Tests) |
| `dotnet test tests/DpiBypass.Tests -c Release` | ✅ **716 geçti**, 0 başarısız (taban: 654) |
| `pwsh -File scripts/tests/install.tests.ps1` | ✅ |
| `pwsh -File scripts/tests/xaml-resources.tests.ps1` | ✅ 88 anahtar, 361 referans |
| `pwsh -File scripts/tests/latency-harness.tests.ps1` | ✅ 12 test (yeni) |
| `dotnet format --verify-no-changes` | ⚠️ yalnız taban durumdaki 94 hata (`ControlCommands.cs`); bu değişikliğin dokunduğu dosyaların hepsi temiz |
| Gerçek Windows entegrasyonu | ❌ **NOT RUN** — §5 |

---

## 8. Fiziksel sınırlar

Değişmedi ve tekrar edilmelidir:

- **Coğrafi mesafe.** İstanbul'dan Frankfurt'a ışık hızı sınırı yaklaşık 30 ms
  gidiş-dönüştür. Hiçbir Windows ayarı bunu değiştiremez.
- **ISP rotası.** Operatörün peering'i ve rota seçimi bu makinede
  değiştirilemez.
- **İndirme kuyruğu.** Operatörün ekipmanındadır; yerel bir sınır ona ulaşmaz
  (§4).
- **Boştaki ping.** Çoğu bağlantıda yerel olarak kazanılacak bir şey yoktur ve
  doğru cevap "kazanç bulunamadı"dır — bu bir hata değil, bir ölçüm sonucudur.

Kazanılabilecek yer, yük altındaki kuyruklanmadır; ve orada bile kazanç
**ölçülür**, varsayılmaz.
