<div align="center">

<img src="assets/logo/dpibypass-256.png" width="128" alt="DPI Bypass" />

# DPI Bypass

**Türkiye'deki DPI (derin paket denetimi) engellerini aşan, ağ değiştiğinde
yöntemi kendiliğinden yeniden bulan Windows uygulaması.**

Discord başta olmak üzere HTTPS/HTTP bağlantılarını, ping ve ses trafiğine
dokunmadan çalışır hâle getirir. VPN değildir: trafik başka bir ülkeye çıkmaz.

Geliştirici: **Atom Gamer Arda A.G.A**

</div>

---

## Tek satırla kurulum

PowerShell'i açın ve şunu yapıştırın:

```powershell
irm https://raw.githubusercontent.com/ATOMGAMERAGA/DPI-Bypass-Windows/main/scripts/install.ps1 | iex
```

Betik son sürümü indirir, yayınlanan SHA256 listesiyle doğrular, sessizce kurar
ve uygulamayı açar. Yönetici hakkı gerekiyorsa kendisi yükseltilmiş bir pencere
açar.

**Aynı komut güncelleme komutudur.** Betik kurulu sürümü GitHub'daki son sürümle
karşılaştırır:

| Durum | Yapılan |
| --- | --- |
| GitHub'daki sürüm daha yeni | İndirilip doğrulanır, **eski kurulum kaldırılır**, yenisi kurulur. Ayarlarınız korunur |
| En güncel sürüm zaten kurulu | "Zaten en güncel sürüm kurulu" denir ve hiçbir şey indirilmez |
| Kurulu sürüm yayınlanandan yeni | Dokunulmaz (kendi derlemeniz olabilir) |

Aynı sürümü yeniden kurmak için komutu `-Force` ile çalıştırın:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/ATOMGAMERAGA/DPI-Bypass-Windows/main/scripts/install.ps1))) -Force
```

Kurulum dosyasını elle indirmeyi tercih ederseniz
[Releases](../../releases/latest) sayfasındaki
`DpiBypass-Setup-<sürüm>.exe` dosyasını çalıştırın. Kurulum istemeyenler için
aynı sayfada `DpiBypass-Portable-<sürüm>.zip` de bulunur.

## Ne yapıyor?

Türkiye'de Discord gibi servisler DNS seviyesinde **ve** DPI seviyesinde
engellenir: TLS el sıkışmasının ilk paketindeki alan adı (SNI) okunur ve
bağlantı ya sıfırlanır ya da sessizce düşürülür. DPI Bypass bu iki katmanı
birlikte ele alır.

| Katman | Yapılan iş |
| --- | --- |
| **DNS** | Sorgular **DNS-over-HTTPS** ile taşınır: Cloudflare birincil, Google ve Quad9 yedek. Çözümleyicilere IP adresiyle bağlanıldığı için TLS içinde alan adı hiç gönderilmez; DNS trafiği alan adına göre süzülemez. Yanıtlar yerel olarak önbelleğe alınır. |
| **DPI** | Bağlantının yalnızca **ilk veri paketi** (TLS ClientHello ya da düz metin HTTP istek başlığı) çekirdek süzgeciyle yakalanır ve denetleyicinin akışı birleştirememesi için yeniden şekillendirilir. |
| **QUIC** | İsteğe bağlı olarak korunan programların yeni QUIC el sıkışmaları reddedilir; tarayıcı saniyeler içinde atlatma uygulanabilen TCP'ye döner. Kurulmuş QUIC oturumlarına dokunulmaz. |
| **Diğer trafik** | Hiç işlenmez. ICMP (ping), UDP, Discord ses trafiği ve indirme akışı çekirdek süzgecine girmez bile. |

### Atlatma yöntemleri

| Yöntem | Ne yapar |
| --- | --- |
| **Bölme** | Alan adının tam ortasından iki TCP parçasına ayırır; hiçbir paket düşmediği için ek gecikme getirmez |
| Ters sıralı bölme | Parçaları ters sırada gönderir |
| Sahte paket (düşük TTL) | Denetleyiciye zararsız bir el sıkışma gösterir; paket sunucuya varmadan TTL ile ölür |
| Sahte paket (geçersiz sıra no) | Sunucunun pencere dışı sayıp attığı bir kopya gönderir |
| Sahte paket (bozuk sağlama) | Sunucunun sağlama hatası nedeniyle attığı bir kopya gönderir |
| Üç parçalı bölme | İki noktadan keser |
| Bant dışı bayt | URG bayrağıyla tek bir bayt önden gönderir |
| HTTP başlık oyunları | `Host:` başlığının yazımını ve ayırıcı boşluğunu değiştirir |

Bu yöntemler kombinasyonlarıyla birlikte **14 hazır tarif** oluşturur
(`DpiBypass.exe strategies`).

Her tarifin ortak bir kuralı vardır: paketin **bayt sayısı değişmez**. Motor
yalnızca giden paketleri görür, dolayısıyla Windows'un TCP yığını kendi
gönderdiği bayt sayısını bilir. Bayt eklemek (örneğin ClientHello'yu birden çok
TLS kaydına bölmek, kayıt başına 5 bayt başlık ekler) sunucunun hiç
gönderilmemiş veriyi onaylamasına yol açar; Windows böyle bir onayı atar ve
bağlantı zaman aşımına kadar asılı kalır. Bu yüzden yeniden çerçeveleme yerine
yalnızca **yeniden bölme, sıralama ve yerinde bayt değişimi** kullanılır.

## Kendi kendine ayar bulması

Uygulama hangi yöntemin çalıştığını varsaymaz, **ölçer**:

1. Bulunduğunuz ağın kimliği çıkarılır (Wi-Fi adı, ağ geçidinin MAC adresi,
   bağlantı türü).
2. Operatör otomatik algılanır (ters DNS, Team Cymru üzerinden ASN ve ağ adı
   ipuçlarıyla) ve o operatöre uygun yöntem sıralaması seçilir.
3. Önce hiç dokunmadan denenir — ağ zaten engellemiyorsa hiçbir şey yapılmaz.
4. Aksi hâlde adaylar tek tek uygulanır ve her biri için **gerçek bir
   discord.com TLS el sıkışması** yapılır. Sertifika da doğrulanır, böylece
   araya giren bir kutu "başarılı" sayılmaz.
5. Çalışanlar arasından **en hızlısı** seçilir ve o ağ için hatırlanır.

**Ağ değiştiğinde** (örneğin `atom` adlı ağdan `atoms hotspot` adlı ağa
geçtiğinizde) bu arka planda kendiliğinden yeniden çalışır. O ağ daha önce
görüldüyse kayıtlı yöntem önce denenir; hâlâ çalışıyorsa saniyeler içinde hazır
olur, çalışmıyorsa yeni bir arama başlar.

Ayrıca **düzenli denetim** (varsayılan 30 dakika) seçili yöntemi yeniden sınar;
operatör kural değiştirdiğinde yeni bir arama kendiliğinden başlar.

Desteklenen operatör profilleri: Türk Telekom (Mobil / Evde İnternet /
Hotspot), Redbox, Turkcell (Mobil / Superonline / Superbox / Hotspot),
Vodafone (Mobil / Evde İnternet / Hotspot), TurkNet ve "Diğer / Bilinmiyor".

## Her sitede çalışması

- Yerleşik listede Discord'un tüm alan adları ve Türkiye'de DPI ile
  engellendiği bilinen diğer adresler vardır.
- **Otomatik keşif:** açtığınız yeni bir alan adı, atlatmasız açılmayıp
  atlatmayla açılıyorsa sessizce sınanır ve kalıcı olarak listeye eklenir.
  Ölçüm sırasında yalnızca o alan adı etkilenir; diğer bağlantıların koruması
  bir an bile düşmez.
- **Siteler** sekmesinden dilediğiniz alan adını elle ekleyebilir, yerleşik
  listeden çıkarabilirsiniz. Alt alan adları kendiliğinden kapsanır.

## Neyin korunacağını siz seçiyorsunuz

Arayüzdeki **Kapsam** sekmesinde dört seçenek var:

- **Yalnızca Discord** — sadece Discord uygulamasının trafiği ve Discord alan
  adları. Sisteme etkisi en düşük seçenek. Kurulu Discord sürümleri (kararlı,
  PTB, Canary, Microsoft Store) otomatik algılanır ve trafik, paketi açan
  sürecin kimliğine göre eşleştirilir.
- **Engelli site listesi (önerilen)** — yerleşik, öğrenilen ve elle eklenen tüm
  alan adları, hangi program açarsa açsın korunur.
- **Engelli siteler + tarayıcılar** — buna ek olarak kurulu tarayıcılardaki tüm
  siteler.
- **Tüm sistem** — bilgisayardaki bütün programlar.

## Mobil hotspot uyumluluğu ve tanılama

**DNS ve ayarlar → Mobil hotspot uyumluluğu ve tanılama** kartı, telefon
paylaşımı ve mobil veri bağlantılarını **kalıcı ağ ayarı değiştirmeden** inceler:

Tanılama kalıcı ağ ayarı veya trafik sınıflandırma kuralı değiştirmez; ölçüm için
sıradan ICMP, DNS ve bağlantı denetimi paketleri gönderir.

- IPv4 ve IPv6 adresi var mı, trafik geçiyor mu;
- ad çözümleme (DNS) çalışıyor mu;
- median / p95 RTT ve paket kaybı;
- parçalanmadan geçen en büyük IPv4 ICMP yükü (MTU sorunu, "sayfa yarım
  yükleniyor"un sık sebeplerinden biri);
- yerel bağdaştırıcı adresi 100.64/10 paylaşılan adres alanında mı (bu gözlem
  telefonun veya operatörün yukarısındaki CGNAT'ı tek başına kanıtlamaz);
- etkin olabilecek bir VPN/tünel bağdaştırıcısı var mı (en iyi çaba tespiti).

```powershell
DpiBypass.exe hotspot            # durum
DpiBypass.exe hotspot diagnose   # bağlantıyı incele
DpiBypass.exe hotspot cleanup    # eski TTL yapılandırmasını temizle
```

**Plan / hotspot hakkınız "Bilinmiyor" olarak raporlanır.** TTL, SSID, operatör
adı, APN ve IP aralığı operatörün kendi sebepleriyle ayarladığı şeylerdir;
hiçbiri bir aboneliğin neyi kapsadığını göstermez. Uygulama tahmin etmez.

### Kaldırılan: hotspot TTL düzeltmesi ("Vodafone sınırsız modu")

Eski sürümlerde, paylaşılan bağdaştırıcıdan çıkan paketlerin TTL'ini yeniden
yazan ve giden IPv6'yı düşüren bir mod vardı. Amacı operatörün paylaşım
sayacını tanımaz hale getirmekti. **Bu mekanizma kaldırıldı.**

Yükseltme yapan bir kurulumda önemli olan kısım korunur: eski bir ayar dosyası
**her açılışta** otomatik olarak temizlenir.

```
eski yapılandırma bulundu
    ↓
TTL yeniden yazımı kapatılır
    ↓
kayıtlı ağ listesi silinir
    ↓
mod kullanılıyorduysa yerine hotspot tanılaması açılır
    ↓
temizlenmiş dosya diske yazılır
```

Geçiş yalnızca eski alanların bir fonksiyonudur ve idempotenttir: ikinci kez
çalıştırmak hiçbir şey yapmaz, bir işaretçiye bakmadığı için de yedekten dönen
ya da elle düzenlenen bir dosya modu geri getiremez. `DpiBypass.exe vodafone off`
komutu çalışmaya devam eder ve aynı temizliği yapar.

## Ping düşürme (Beta)

**DNS ve ayarlar → Ping düşürme** kartındaki özellik, aktif fiziksel ağ
bağdaştırıcısının desteklediği güvenli NIC seçeneklerini **eşli A/B ölçümüyle**
tek tek sınar.

### Ölçüm

Her ölçüm, aynı hedefe **peş peşe ve sabit aralıkla** 24 ICMP probe'u gönderir
(batch hâlinde değil: eşzamanlı gönderim ağ kadar makinenin kendi gönderim
kuyruğunu da ölçer). Hesaplananlar: minimum, **median, p95, p99**, jitter
(ardışık farkların ortalaması), paket kaybı, gateway median/p95 ve ölçüm
sırasında bağdaştırıcının taşıdığı trafik. ICMP engelliyse internet ölçümü
açıkça `TCP/443` olarak etiketlenen bağlantı süresiyle devam eder.

### Eşli A/B

Tek bir önce/sonra çifti, 2 ms'lik bir iyileşmeyi 2 ms'lik bir dalgalanmadan
ayıramaz. Bu yüzden her aday için tur tur ölçülür:

```
A1 ölç (ayarsız) → uygula → B1 ölç → geri al
A2 ölç (ayarsız) → uygula → B2 ölç → geri al
                 ...
```

Bir ayar ancak **aynı metrikteki kazanç turların çoğunda tekrarlanır ve turların
birbiriyle olan uyuşmazlığından büyükse** kabul edilir. Sonuç kararsızsa
(kazanç var ama tutarsız) fazladan tur çalıştırılır; hâlâ kararsızsa cevap
"hayır"dır. Kabul edilen bir ayar, bir sonraki adayın ölçüldüğü zemin olur, ve
kullanıcıya gösterilen iyileşme **eşli turların** sonucudur — tek bir son
örneğin değil.

Bir çiftin iki yarısı farklı yük altında ölçüldüyse (biri boşta, diğeri indirme
sırasında) o tur **atılır ve tekrarlanır**: aksi hâlde ölçülen şey ayar değil,
indirmedir.

### Reddetme

Şunlardan herhangi biri adayı anında bitirir: uzak ucun yanıt vermemesi, paket
kaybının bir probe'dan fazla artması, median / p95 / p99'da anlamlı gerileme,
sürücünün değeri canlı uygulamaması, geri alınamayan bir yazma. CPU maliyeti
olan bir ayar (Interrupt Moderation kapalı) **iki kat** büyük bir kazanç
göstermek zorundadır.

### Nerede olduğunu söyler

Gecikmenin ne kadarının ilk atlamada, ne kadarının operatör ve internet yolunda
olduğu ayrıştırılır. İlk atlama 1 ms ve uzak uç 70 ms ise hiçbir bağdaştırıcı
ayarı bunu değiştirmez — ve bunu söylemek sekiz ayarı deneyip bir şey bulamamaktan
daha faydalıdır. Kendi trafiğiniz aktifken gecikme belirgin artıyorsa bu
**kuyruklanma** olarak raporlanır; çözümü gönderim hızını sınırlamaktır, NIC
ayarı değil.

### Dokunulan ve dokunulmayan

Sürücü gerçekten destekliyorsa denenenler:

- `SelectiveSuspend`, `DeviceSleepOnDisconnect` ve `D0PacketCoalescing`
  (`Get/Set-NetAdapterPowerManagement`);
- yalnız fiziksel Ethernet'te NDIS `*InterruptModeration` registry keyword'ü
  (`Get/Set-NetAdapterAdvancedProperty`).

RSS, checksum/LSO/RSC offload, MTU, TCP autotuning, ECN, Nagle/registry hack'leri,
HPET/timer ayarları, DNS, IPv6, route/metric, QoS, firewall ve işlem önceliği
değiştirilmez. Bağdaştırıcı kapatılıp açılmaz ve yeniden başlatılmaz. VPN,
TAP/TUN, Hyper-V, Docker ve WSL sanal bağdaştırıcıları atlanır. Paket yoluna
hiç dokunulmaz: bu özellik tek bir WinDivert tanıtıcısı açmaz, oyun ve ses
trafiği normal Windows ağ yolunda kalır.

Bu özellik ISP rotasını değiştirmez, VPN değildir ve uzaktaki oyun sunucusunu
fiziksel olarak yakınlaştırmaz. Doğrulanmış kazanç yoksa doğru sonuç
**"Bu ağda doğrulanmış bir gecikme iyileşmesi bulunamadı. Özgün ayarlar geri
yüklendi."** mesajıdır.

### Profil ve kurtarma

Doğrulanan sonuç **ağ + bağdaştırıcı + sürücü yetenek parmak izi** üçlüsüne
bağlanarak `latency-profiles.json` içinde saklanır. Aynı ağa dönüldüğünde
tam ölçüm yerine kayıtlı ayarlar yeniden uygulanır ve tek bir doğrulama ölçümüyle
onaylanır; onaylanmazsa geri alınır ve profil silinir. Farklı bir bağdaştırıcı,
sürücü güncellemesi veya bir aydan eski bir kayıt geçersizdir — Ethernet'te
kanıtlanan bir ayar yanındaki Wi-Fi kartı için hiçbir şey söylemez. Dosyada
adres, SSID veya BSSID tutulmaz ve hiçbir yere gönderilmez.

Özgün değerler `C:\ProgramData\DPI Bypass\latency-snapshot.json` içinde,
her adımdan **önce** yazılan bir durum damgasıyla tutulur
(`SnapshotCreated → CandidateApplied → Verifying → Committed`). Mod
kapatıldığında, ağ değiştiğinde, uygulama normal kapandığında ve kaldırma
başlamadan önce geri yüklenir. Yarım kalmış bir çalışma — crash, elektrik
kesintisi, süreç sonlandırma — sonraki açılışta **modun açık olup olmadığına
bakılmaksızın** geri alınır; kayıp bağdaştırıcı varsa kurtarma bilgisi silinmez.

## Ping ve hıza etkisi

Tasarım gereği yok denecek kadar az:

- Çekirdek süzgeci yalnızca **giden, 80/443 hedefli, veri taşıyan TCP
  paketlerini** alır. ICMP (ping), UDP, Discord ses trafiği ve indirme akışı bu
  sürece hiç girmez.
- Girenler arasında da yalnızca **ilk** paket (ClientHello / HTTP istek başlığı)
  işlenir; el sıkışma bittikten sonraki hiçbir bayta dokunulmaz.
- Bağlantı başına durum tutulmaz, dolayısıyla büyüyen bir tablo ya da arama
  maliyeti yoktur.
- Trafik bir vekil sunucudan (proxy) veya VPN'den geçmez; paketler doğrudan
  hedefe gider.
- Otomatik ayarlama, çalışan yöntemler arasından en düşük gecikmeliyi seçer;
  bölme yöntemleri hiçbir paket düşürmediği için sıfır ek gecikme getirir.

## Ekran arayüzü

Windows 11'in Fluent görünümünü ve Mica malzemesini kullanır, sistem
açık/koyu temasını canlı olarak izler. Logo 16 pikselden 1024 piksele kadar her
boyutta ayrı ayrı gömülüdür ve arayüzde yüksek çözünürlüklü kaynaktan çizilir;
böylece tepside, görev çubuğunda, kurulum sihirbazında ve %350 ölçeklemede
bulanıklaşmaz.

Sekmeler: **Durum** (durum, aç/kapat, discord.com testi, sayaçlar), **Kapsam**,
**Siteler**, **Ağ ve yöntem**, **DNS ve ayarlar**, **Günlük**.

Pencereyi kapatmak korumayı durdurmaz; uygulama tepside çalışmaya devam eder.
Kısayolu yeniden çalıştırmak ya da tepsi simgesine tıklamak pencereyi geri
getirir.

> **Simgeyi göremiyor musunuz?** Windows 11, ilk kez gördüğü bildirim alanı
> simgelerini saatin yanındaki **^** okunun altına gizler. Oku açıp simgeyi
> görev çubuğuna sürüklerseniz kalıcı olarak orada durur. Simge olmasa da
> kısayolu yeniden çalıştırmak pencereyi her zaman öne getirir.

Uygulama, pencereyi ilk kez göstermeden tepside başlamaz: kurulumdan sonraki ilk
çalıştırma ve ilk oturum açma her zaman pencereyi açar. Sonraki açılışlarda
"Açılışta pencereyi göstermeden tepside başla" ayarı geçerlidir. Pencereyi her
koşulda açmak için `DpiBypass.exe --show` kullanılabilir.

## Komut satırı

```powershell
DpiBypass.exe --show              # pencereyi her koşulda aç
DpiBypass.exe status              # genel durum
DpiBypass.exe test [alanadı]      # erişimi sına (varsayılan: discord.com)
DpiBypass.exe search              # yöntemi yeniden ara
DpiBypass.exe domains             # korunan alan adları
DpiBypass.exe strategies          # yöntem kataloğu
DpiBypass.exe isps                # operatör profilleri
DpiBypass.exe enable / disable    # korumayı aç / kapat
DpiBypass.exe hotspot diagnose    # mobil paylaşım bağlantısını incele
DpiBypass.exe hotspot cleanup     # eski hotspot TTL yapılandırmasını temizle
DpiBypass.exe latency status      # düşük-gecikme durumu
DpiBypass.exe latency on / off    # ölçümlü optimizasyonu aç / kapat
DpiBypass.exe latency test        # kalıcı ayar değiştirmeden ölç
DpiBypass.exe latency restore     # özgün NIC değerlerini kurtar
DpiBypass.exe restore-dns         # DNS ayarlarını geri yükle
DpiBypass.exe --health-check [sn] # çalışan kopyanın penceresini açmasını bekle
DpiBypass.exe --ui-selftest       # arayüzü sına ve çık (ağ motorunu açmaz)
```

`--health-check` tek örnek kilidini almaz: yalnızca çalışan kopyadan penceresini
açmasını ister ve pencere gerçekten göründüğünde `0`, çalışan kopya pencereyi
açamazsa `1`, çalışan bir kopya yoksa `2` döner. Henüz açılmakta olan bir kopya
durumunu bildirdiği için beklenir —
kurulum betiği de kurulumun başarılı sayılıp sayılmayacağına bununla karar verir.
"Gerçekten göründü" burada tam anlamıyla ilk karenin çizilmiş olmasıdır: yalnızca
pencere tanıtıcısı oluşmuş, hiç çizilmemiş bir pencere `0` döndürmez.

`--ui-selftest` uygulamayı normal biçimde açar, penceresinin gerçekten çizilip
çizilmediğini ölçer ve sonucu çıkış koduyla bildirip kapanır. Paket sürücüsünü
açmaz, DNS'e dokunmaz ve denetim kanalını başlatmaz; bir arıza bulursa pencere
durumunu ve açılış izlemesini `%ProgramData%\DPI Bypass\logs\ui-diagnostics.log`
dosyasına yazar. Başka bir kopya çalışmıyorken çalıştırın.

Durum ve denetim komutları, adlandırılmış bir kanal üzerinden **çalışan
uygulamaya** bağlanır — ayarları dosyadan okuyup tahmin etmez. Uygulama
kapalıysa komut bunu söyler. Çıktının konsola yazılabilmesi için komutu
**yönetici olarak açılmış** bir PowerShell/cmd penceresinden çalıştırın;
yükseltilmemiş bir konsoldan çalıştırıldığında sonuç bir iletişim kutusunda
gösterilir.

## Gereksinimler

- Windows 10 sürüm 1809 veya daha yenisi / Windows 11, 64 bit
- Yönetici hakları (ağ sürücüsü açmak için zorunlu)
- .NET kurmanız gerekmez; uygulama çalışma zamanını kendi içinde taşır

## Otomatik başlatma

Kurulumda uygulama Windows'un **Başlangıç Uygulamaları** listesine eklenir. Bu
kayıt, **yükseltilmiş** çalışan `DpiBypass-Autostart` görevini tetikler; böylece
her açılışta yönetici onayı sorulmaz ve Windows Ayarları'ndaki anahtar gerçekten
açılışı denetler. Görev kaydedilemezse uygulama doğrudan `Run` anahtarına düşer —
o durumda onay istenir. İstemiyorsanız Windows Başlangıç Uygulamaları'ndan veya
**DNS ve ayarlar** sekmesinden kapatabilirsiniz.

## Ayarlar

`C:\ProgramData\DPI Bypass\`

| Dosya | İçerik |
| --- | --- |
| `settings.json` | Kapsam, DNS kipi, yöntem seçimi, Ping düşürme ve hotspot tanılaması, başlangıç seçenekleri |
| `networks.json` | Ağ başına öğrenilen yöntem belleği |
| `learned-domains.json` | Otomatik keşfin bulduğu engelli alan adları |
| `dns-snapshot.json` | Değiştirilmeden önceki DNS ayarlarınız |
| `latency-snapshot.json` | Ping düşürmenin değiştirdiği NIC özelliklerinin tam özgün değerleri ve işlem durumu |
| `latency-profiles.json` | Ağ + bağdaştırıcı + sürücü başına doğrulanmış ölçüm sonuçları (yalnız yerel) |
| `logs\` | Günlük kayıtları (14 gün saklanır) |

## Kaldırma

Ayarlar → Uygulamalar üzerinden normal şekilde kaldırılır. Kaldırma sırasında
özgün NIC ve DNS ayarlarınız geri yüklenir, oturum açma görevi silinir ve
WinDivert sürücü servisi kaldırılır.

## Kaynaktan derleme

```powershell
git clone https://github.com/ATOMGAMERAGA/DPI-Bypass-Windows.git
cd DPI-Bypass-Windows

./tools/fetch-windivert.ps1                          # sürücü dosyalarını indirir
dotnet test tests/DpiBypass.Tests/DpiBypass.Tests.csproj
./scripts/tests/install.tests.ps1                    # kurulum/güncelleme kararları
./scripts/tests/xaml-resources.tests.ps1             # arayüz kaynakları eksiksiz mi
dotnet publish src/DpiBypass.App/DpiBypass.App.csproj -c Release -o artifacts/publish
```

Üçü de CI hattında her derlemede çalışır.

Kurulum paketi için Inno Setup 6 gerekir:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0.0 /DPublishDir=..\artifacts\publish installer\DpiBypass.iss
```

Logo dosyalarını yeniden üretmek için (Python + Pillow):

```bash
python3 tools/generate_assets.py assets/logo/source.png
```

### Proje yapısı

| Yol | İçerik |
| --- | --- |
| `src/DpiBypass.Core` | Paket motoru, DNS, operatör profilleri, otomatik ayarlama, gecikme ölçümü, hotspot tanılaması, denetim kanalı |
| `src/DpiBypass.App` | WPF arayüzü, tepsi simgesi, otomatik başlatma, komut satırı |
| `tests/DpiBypass.Tests` | Birim testleri |
| `installer/` | Inno Setup betiği ve sihirbaz görselleri |
| `tools/` | Sürücü indirme ve logo üretme betikleri |
| `.github/workflows/` | Derleme, test ve sürüm yayınlama hattı |

Motorun ana parçaları:

```
src/DpiBypass.Core/
  ProtectionService.cs        düzenleyici: saptama, arama, olay akışı
  Engine/BypassEngine.cs      paket yolu (WinDivert)
  Engine/DesyncPlan.cs        stratejinin pakete uygulanması
  Engine/StrategyLibrary.cs   yöntem kataloğu
  Net/TlsRecordFragmenter.cs  ClientHello'yu birden çok TLS kaydına bölme
  Net/TlsClientHello.cs       ClientHello ayrıştırma
  Dns/DohResolver.cs          DNS-over-HTTPS çözümleyici
  Dns/DnsProxyServer.cs       yerel DNS köprüsü
  Dns/DnsConfigurator.cs      sistem DNS ayarları (ve geri alma)
  Network/IspProfile.cs       operatör profilleri
  Network/LatencyOptimizer.cs eşli A/B turlarıyla ölç, uygula, doğrula, rollback et
  Network/LatencyComparison.cs bir adayın kabul/ret kuralı
  Network/LatencyStatistics.cs median, p95, p99, jitter, kayıp
  Network/LatencyProbe.cs     sıralı ve sabit aralıklı RTT ölçümü
  Network/LatencyProfileStore.cs ağ + bağdaştırıcı + sürücü başına doğrulanmış sonuç
  Network/NetworkLoadSampler.cs ölçüm penceresinde hattın ne kadar meşgul olduğu
  Diagnostics/StrategyTuner.cs        gerçek bağlantı testleriyle yöntem arama
  Diagnostics/BlockedSiteDiscovery.cs yeni engelli siteleri ölçerek bulma
  MobileHotspot/MobileHotspotDiagnostics.cs salt-okunur bağlantı incelemesi
  MobileHotspot/HotspotLegacyMigration.cs eski TTL yapılandırmasının temizliği
  Ipc/ControlServer.cs        uygulama ↔ komut satırı protokolü
```

### Sürümleme

`Directory.Build.props` içindeki `VersionPrefix` ana sürümü belirler; `main`
dalına yapılan her birleştirme, sonuna CI çalışma numarası eklenmiş yeni bir
sürüm (`1.0.0.42` gibi) olarak otomatik yayınlanır.

## Sorun giderme

| Belirti | Bakılacak yer |
| --- | --- |
| Kısayola tıklıyorum, pencere açılmıyor | Uygulama zaten tepside çalışıyordur; kısayolu yeniden çalıştırmak çalışan kopyanın penceresini öne getirir. Yine açılmıyorsa `%ProgramData%\DPI Bypass\logs\crash.log` ve o günün `.log` dosyasındaki "Açılış kararı" / "Görünürlük denetimi" satırlarına bakın |
| Uygulama çalışıyor ama hiçbir yerde görünmüyor | Bildirim alanı simgesi **^** okunun altında olabilir. `DpiBypass.exe --show` pencereyi her koşulda açar. Görev çubuğunda düğme var ama pencere yoksa `DpiBypass.exe --ui-selftest` neyin eksik olduğunu (ilk kare, DWM gizlemesi, ekran dışı konum) söyler ve `%ProgramData%\DPI Bypass\logs\ui-diagnostics.log` dosyasına yazar |
| Görev çubuğunda düğme var, pencere hiç görünmüyor | Uygulama artık bunu kendisi fark ediyor: ilk kare çizilene kadar pencere "açıldı" sayılmaz, sınırlı sayıda kurtarma denemesi yapılır (öne getir → ekrana taşı → arka planı düz renge al → gerekirse pencereyi bir kez yeniden oluştur) ve hepsi başarısız olursa günlük klasörünü gösteren bir ileti çıkar. Günlükte "Pencere erişilemiyor" ve "Açılış izlemesi" satırlarına bakın |
| Kurulum bitince pencere bir an açılıp hemen kapanıyor | v1.0.0.51 ve öncesinde kurulum uygulamayı başlatıyor, tek satırlık komut da bir saniye sonra ikinci bir kopya başlatıyordu. Henüz açılmakta olan ilk kopya "buradayım" diyemediği için ikinci kopya onu kapalı sayıp kapatıyordu — ekranda görülen tam olarak budur. Sonraki sürümlerde açılmakta olan kopya durumunu baştan bildiriyor ve beklenip kapatılmıyor. Sürümü güncelleyin; sürerse günlükteki "Çalışan kopya henüz açılıyor" satırlarını bildirin |
| Uygulama açılırken donuyor, yaklaşık 40 saniye sonra kayboluyor | Aynı arızanın devamıydı: açılış hatasında uygulama, hatayı yazmadan önce DNS'i geri almak için 38 saniyeye kadar bekliyordu. Artık önce `%ProgramData%\DPI Bypass\logs\crash.log` yazılıp hata gösteriliyor, DNS kurtarma ayrı `DpiBypass.Recovery.exe` sürecine devrediliyor. Bu dosyanın içeriğini bildirin |
| Kurulumdan hemen sonra "yol bulunamadı" gibi bir hata çıkıyor | Kurulum, uygulamayı silmek üzere olduğu geçici klasörden başlatıyordu; uygulama artık her başlangıçta kendi klasörüne geçiyor ve yardımcı programları tam yolla çağırıyor. Güncel sürümde görülmemeli — görülüyorsa o günün günlüğündeki ilk iki satırı (sürüm, klasör, komut satırı) bildirin |
| `Kurulum 1 kodu ile sonlandı` | Inno Setup'ın "kurulum başlatılamadı" kodudur: kurulum betiği daha ilk adımda durmuştur. v1.0.0.47'de kurulum betiği, sihirbaz klasörü seçmeden önce `{app}` sabitini genişlettiği için hiçbir makinede kurulamıyordu; sonraki sürümlerde giderildi, tek satırlık komutu yeniden çalıştırmak yeter. Yine görürseniz komutun yazdırdığı `%TEMP%\dpibypass-setup-*.log` dosyasının son satırlarını bildirin |
| Pencere açılıyor ama içi boş / saydam görünüyor | Windows'ta *Ayarlar → Kişiselleştirme → Renkler → Saydamlık efektleri* kapalıysa ya da donanım hızlandırma yoksa uygulama düz renkli arka plana kendiliğinden geçer — pencere zaten açıkken de denetlenir. Geçmediyse günlükteki "Pencere arka planı" satırını bildirin |
| Pencere geç geliyor / bir süre boş duruyor | Çalışan kopya meşgulse elle açılan kısayol artık onu kapatmak yerine bekliyor, ve motor günlükleri arayüzü kilitlemeyecek şekilde toplu işleniyor. Sorun sürüyorsa günlükteki "Çalışan kopya meşgul" satırına bakın |
| "Başka bir kullanıcı oturumunda çalışıyor" | Koruma bilgisayar başına tek kopyadır. Diğer Windows oturumunda açık olan kopyayı kapatın |
| "Yönetici hakları gerekiyor" | Uygulamayı yönetici olarak çalıştırın; sürücü aksi hâlde açılamaz |
| Durum "engel sürüyor" diyor | **Ağ ve yöntem** → *Yeniden tara*. Çalışan bulunmazsa DNS modunu veya kapsamı değiştirip yeniden deneyin |
| Tarayıcıda açılmıyor, uygulamada açılıyor | Kapsamı **Engelli siteler + tarayıcılar** yapın ve QUIC engellemesini açık bırakın |
| DNS bozuk kaldı | Uygulamayı bir kez çalıştırıp kapatın; `DpiBypass.exe restore-dns` de ayarları geri yükler |
| Telefon paylaşımında bazı sayfalar yarım yükleniyor | **DNS ve ayarlar → Mobil hotspot uyumluluğu ve tanılama** → *Tanıla*. 1500 baytlık paketler geçmiyorsa rapor ölçülen parçalanmasız sınırı söyler; yalnızca belirti varsa bu sınıra yakın bir MTU denenip yeniden doğrulanmalıdır |
| "Vodafone sınırsız modu" nereye gitti | Kaldırıldı. Eski ayar dosyanız her açılışta otomatik temizlenir; `DpiBypass.exe vodafone off` de aynı temizliği yapar. Yerine gelen tanılama bağlantıyı değiştirmeden inceler |
| Günlükler | **Günlük** sekmesi → *Klasörü aç* (`C:\ProgramData\DPI Bypass\logs`) |

## Yasal not

Bu araç, kullanıcının kendi internet bağlantısı üzerinde hangi paketlerin nasıl
biçimlendirileceğini belirlemesini sağlar; başkalarının sistemlerine erişmek,
kimlik doğrulamayı atlatmak veya trafiği izlemek için bir mekanizma içermez.
Bulunduğunuz yerdeki mevzuata uymak kullanıcının sorumluluğundadır.

## Lisans

Bu depodaki kod [LICENSE](LICENSE) dosyasındaki koşullara tabidir. Birlikte
dağıtılan üçüncü taraf bileşenler ve lisansları
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasında listelenmiştir.
