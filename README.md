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

## Vodafone Sınırsız Modu

**DNS ve ayarlar → Vodafone Sınırsız Modu** kartı iki işi bir arada yapar:
kaydettiğiniz ağlarda **giden paketlerin TTL değerini düzeltir**, ve aynı
bağlantıyı **kalıcı ağ ayarı değiştirmeden** inceler.

### Ne yapıyor

Operatör, paylaşımı paketin TTL değerinden anlar: telefonun kendi trafiği 64 ile
ulaşırken, paylaşılan bağlantıdan gelen paket telefonda bir kez yönlendirildiği
için 63 olarak varır. Mod açıkken giden paketler **65** ile yollanır; telefon bir
düşürünce operatöre tam **64** gider. Linux sürümü aynı işi nftables ile yapar
(`src/dpibypass/vodafone.py`), Windows sürümü WinDivert ile yapar — sayılar ve
davranış aynıdır.

Üç koşul birden gerekir, aksi hâlde kural kurulmaz veya kaldırılır:

1. mod açık,
2. bulunduğunuz ağ **kayıtlı ağlar** listesinde,
3. bağdaştırıcı belirlenebiliyor.

Ev Wi-Fi'ına geçtiğinizde kural kendiliğinden kalkar — kimsenin hop saymadığı bir
ağda TTL yeniden yazmak trafiğinizi değiştirir ve size bir şey kazandırmaz.

Kural **çekirdekte kalıcı bir ayar bırakmaz**: modu kapattığınızda, ağ
değiştirdiğinizde ya da uygulama kapandığında kaldırılır. Windows'ta
**yönetici hakkı** ve **WinDivert sürücüsü** gerekir; ikisinden biri yoksa kart
"Aktif" demez, kuralın neden kurulamadığını yazar.

**Maliyeti.** TTL'i kullanıcı alanından yeniden yazmanın tek yolu paketi görmektir:
mod etkinken **yalnızca o bağdaştırıcının** giden paketleri kullanıcı alanına
kopyalanıp geri gönderilir. Kural bu yüzden tek bir bağdaştırıcıya ve elle
kaydettiğiniz ağlara sınırlıdır, ve bu iki koşuldan biri bozulur bozulmaz
kaldırılır. Gelen trafik hiç görülmez.

**Atlatma yöntemleri korunur.** Sahte paket yöntemleri kasıtlı olarak 3-8 gibi
düşük TTL kullanır ve paketin erken ölmesi *yöntemin kendisidir*. Bu yüzden
yalnızca TTL'i **32'nin (koruma eşiği) üstünde** olan paketler yeniden yazılır;
bir yöntem bu eşiği geçerse derleme testte kırılır.

**Giden IPv6.** Telefon paylaşım yaparken bilgisayara ayrı bir genel IPv6 adresi
verir; TTL ne olursa olsun operatör aynı aboneden iki farklı kaynak görür. Bu
yüzden mod etkinken paylaşılan bağdaştırıcıda giden IPv6 varsayılan olarak
düşürülür. Karttan kapatılabilir.

### Tanılama

Kartı açtığınızda bağlı olduğunuz ağı, modun bu ağdaki durumunu ve tek bir
**"Bağlantıyı kontrol et"** düğmesini görürsünüz. Sonuç ham rapor metni olarak
değil, internet erişimi, DNS, bağlantı kalitesi ve plan bilgisi kartları olarak
gösterilir; IPv4/IPv6, MTU, bağdaştırıcı, VPN ve tam rapor "Teknik ayrıntılar"
altındadır. **"Desteklenmiyor", "kullanılmıyor", "ölçülemedi" ve "hata" ayrı
durumlardır** — örneğin çoğu mobil bağlantıda IPv6'nın bulunmaması bir arıza
değildir ve kırmızı gösterilmez.

**"Bu ağı kaydet"** düğmesi bulunduğunuz ağı listeye ekler; bunun için modu
kapatıp yeniden açmanız gerekmez. "Bu ağda kullan" ile "Kayıtlı ağlarda otomatik
kontrol" ayrı şeylerdir ve kartta açıklanır. Modu kapatmak kayıtlı ağları
silmez; bir ağı listeden çıkarmak ayrı bir işlemdir. Ağ değiştiğinizde önceki
ağın sonucu gösterilmez.

**Kayıtlı ağınız kendiliğinden tanınır.** Kart, kayıtlı bir ağdayken başlığında
**"Aktif · \<ağ adı\>"** yazar ve kontrol elle başlatılmayı beklemez:

- Ağ kimliği artık **motordan bağımsız** izlenir. Önceden hangi ağda olduğumuz
  yalnızca koruma çalışırken biliniyordu, bu yüzden koruma kapalıyken kart —
  kullanıcının az önce kaydettiği ağda bile — "kayıtlı değil" diyordu.
- Eşleşme yalnız ağ parmak izine bakmaz, **ağ adına da bakar**. Parmak izi erişim
  noktasının MAC adresini içerir ve telefon paylaşımı her açılışta yeni bir
  rastgele MAC dağıtır (Android ve iOS'ta varsayılan), yani dün kaydedilen ağ
  bugün tanınmayan bir anahtarla geliyordu. Ad eşleştiğinde kayıt bu oturumun
  kimliğiyle güncellenir; liste tek satır kalır.
- Uygulama açıldığında zaten kayıtlı bir ağdaysanız kontrol **bir kez
  kendiliğinden** çalışır (yalnızca "Kayıtlı ağlarda otomatik kontrol" açıkken).

**"Bağlantıyı kontrol et"** hiçbir şeyi değiştirmez: ölçüm için sıradan ICMP, DNS
ve bağlantı denetimi paketleri gönderir, kalıcı ağ ayarı veya trafik
sınıflandırma kuralı oluşturmaz. Değişiklik yapan tek şey yukarıdaki TTL
kuralıdır.

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
DpiBypass.exe hotspot cleanup    # eski alan adlarını güncel ayarlara taşı
DpiBypass.exe vodafone           # mod durumu: kural kurulu mu, kaç paket düzeltildi
DpiBypass.exe vodafone on        # bu ağı kaydet ve TTL kuralını kur
DpiBypass.exe vodafone off       # kuralı kaldır; kayıtlı ağları koru
DpiBypass.exe vodafone diagnose  # aynı tanılamayı Vodafone adıyla çalıştır
```

**Plan / hotspot hakkınız "Bilinmiyor" olarak raporlanır.** TTL, SSID, operatör
adı, APN ve IP aralığı operatörün kendi sebepleriyle ayarladığı şeylerdir;
hiçbiri bir aboneliğin neyi kapsadığını göstermez. Uygulama tahmin etmez.

### Eski ayar dosyaları

Bir ara sürüm (PR #11) TTL yeniden yazımını tamamen kaldırmış, kartın anahtarını
yalnızca salt-okunur tanılamaya bağlamıştı: Windows'ta mod "açık" görünürken
pakete hiç dokunulmuyordu, Linux sürümü ise aynı işi yapmaya devam ediyordu.
Mekanizma geri getirildi ve iki sürüm yeniden aynı davranışı gösteriyor.

Yükseltmede eski alan adları güncel olanlara taşınır: `HotspotTtlFix` →
`VodafoneModeEnabled`, `HotspotTtlValue` → `VodafoneTtl`, `HotspotDropIPv6` →
`VodafoneDropIPv6`, `HotspotTtlNetworks` → `VodafoneModeNetworks`. Kendi
seçtiğiniz TTL ve IPv6 tercihi korunur; kapsam, DNS, başlangıç ve diğer
tercihlere dokunulmaz. Geçiş idempotenttir. PR #11 tarafından daha önce işlenmiş
bir dosyanın Vodafone kimliği de bir kez geri yüklenir; kullanıcı daha sonra modu
kapatırsa tekrar açılmaz. `vodafone off` modu kapatır, `hotspot cleanup` ise
yalnız eski alan adlarını taşır.

Kullanılamayacak bir TTL (koruma eşiğinin altında ya da 255 üstünde) elle
yazılmışsa varsayılana döndürülür ve günlüğe yazılır; mod sessizce çalışmamaz.

## Ping düşürme (Beta)

**DNS ve ayarlar → Ping düşürme** kartındaki özellik iki ayrı şeyi yapar ve
ikisini birbirine karıştırmaz:

1. **Boştaki gecikme:** aktif fiziksel bağdaştırıcının desteklediği güvenli NIC
   ayarlarını, seçtiğiniz hedefe karşı **dönüşümlü A/B ölçümüyle** tek tek sınar.
2. **Yük altındaki gecikme:** siz bir indirme veya gönderim başlattığınızda RTT'ye
   ne olduğunu ölçer, ve isterseniz giden toplu trafiği Windows QoS ile
   sınırlayıp bunun gerçekten işe yarayıp yaramadığını ölçer.

### Kartı kullanmak

Kart tek bir ana düğmeyle çalışır ve düğmenin adı sonuca göre değişir:
**"Bağlantımı analiz et"** ile başlarsınız, sonra sırayla **"Uygun ayarları
dene"**, **"Yük altında test et"**, **"Yeniden ölç"** veya **"Ayarları geri al"**
önerilir. Altında dört kısa kart durur — boştaki ping, yük altındaki ping, ping
dalgalanması ve paket kaybı — ve **ölçülmemiş bir alan boş kalır, sıfır
yazılmaz.** Bunları tek bir durum cümlesi ve tek bir öneri izler.

Çalışan her ölçüm — modu açmak, hızlı ölçüm, yük altında test — **"İptal et"**
ile durdurulabilir; durdurulan çalışma makineyi bulduğu hâle geri getirir.
Geçersiz bir hedef yazdığınızda hata alanın hemen altında görünür ve ölçüm
düğmeleri kapanır, böylece ekranda yeni hedef dururken eski hedefe ölçüm
yapılmaz.

Hangi yolun denendiği ve hangisinin denenmediği kartta ayrı ayrı yazılır:
"Bağlantı ölçümü", "Ağ kartı ayarları", "Yük altında ölçüm" ve "Gönderim
sınırı". Örneğin makinede değiştirilebilecek bir NIC ayarı yoksa bu tek satır
"uygun değil" der; ölçüm ve yük testi kullanılabilir kalır. Teknik seçenekler,
profil yönetimi, uç nokta seçimi, Traffic Guard ayarları ve tam rapor **"Ölçüm
ayrıntıları ve gelişmiş seçenekler"** başlığı altındadır.

Hangi ayarların neden denendiği, hangilerinin bilerek dışarıda bırakıldığı ve her
biri hangi resmî belgeye dayandığı **[LATENCY-RESEARCH.md](LATENCY-RESEARCH.md)**
dosyasında kaynak bağlantılarıyla yazılıdır.

### Neyi ölçtüğünüzü siz seçersiniz

| Hedef | Ne ölçülür | Ne ölçülmez |
| --- | --- | --- |
| **Genel internet referansı** | 1.1.1.1 / 8.8.8.8 / 9.9.9.9 ICMP RTT'si | Oyun sunucunuzun rotası. Bu hedef genel bağlantı sağlığıdır, **oyun sunucusu değildir** |
| **Çalışan oyun / uygulama** | Programın **TCP ve UDP** uç noktaları. UDP oturumları WinDivert'in FLOW katmanından (yalnız gözlem) bulunur; birden fazla aday varsa hangisini ölçeceğinizi siz seçersiniz | Gözlem başlamadan **önce** kurulmuş bağlantılar — WinDivert bu olayları göremez, bu yüzden oyuna yeniden bağlanmanız istenir |
| **Özel sunucu** | `host`, `host:port`, `tcp://host:port`; port 25565 ise Minecraft Java durum sorgusunun **gerçek Ping/Pong süresi** | `udp://host:port` verilirse aynı adrese **rota referansı** ölçülür ve arayüz bunu böyle etiketler |

Bir uç nokta için ölçülen sayının ne olduğu her zaman yazılıdır:

| Araç | Ne ölçer |
| --- | --- |
| ICMP | Aynı adrese giden **rotanın** gidiş-dönüş süresi |
| `TCP/443 (el sıkışma süresi)` | Bağlantı kurma süresi — oturum içi RTT **değildir** |
| `TCP/… (EStats)` | Uygulamanın **zaten açık** olan bağlantısının, yığının kendi ölçtüğü RTT'si. Hiç paket gönderilmez |
| `Minecraft/25565` | Sunucunun kendi yanıt süresi (Server List Ping) |

Hedef deney başında bir kez çözülür ve **sabitlenir**. A ve B kolları aynı IP,
aynı protokol ve aynı portu kullanır — RFC 2681'in Type-P kuralı budur; farklı
uçları ölçen iki sayı birbirinden çıkarılamaz.

### Ölçüm

Her ölçüm aynı uca **peş peşe ve sabit aralıkla** probe gönderir (batch hâlinde
değil: eşzamanlı gönderim ağ kadar makinenin kendi gönderim kuyruğunu da ölçer).
İlk birkaç probe **ısınma** sayılır ve sonuçtan çıkarılır. Hesaplananlar:
minimum, **median, p95, p99**, jitter (ardışık farkların ortalaması), paket
kaybı, gateway median/p95 ve pencere boyunca bağdaştırıcının taşıdığı trafik.
ICMP engelliyse ölçüm açıkça `TCP/443` olarak etiketlenen bağlantı süresiyle
sürer.

**p99, iki kolda da en az 100 geçerli yanıt olmadan karar metriği olamaz.** 40
örnekten hesaplanan bir "p99" en kötü örnektir, yüzdelik değil; arayüz ve JSON
çıktısı bu durumda p99 yerine "örnek yetersiz" der.

### Dönüşümlü A/B (ABBA)

Tek bir önce/sonra çifti, 2 ms'lik bir iyileşmeyi 2 ms'lik bir dalgalanmadan
ayıramaz. Dahası, her turu hep A→B sırasında ölçmek, koşu boyunca kayan her
şeyi (makinenin ısınması, hattın sakinleşmesi) tek bir kola yükler. Bu yüzden
sıra her turda değişir:

```
tur 1:  A ölç (ayarsız) → uygula → bekle → B ölç → geri al → bekle
tur 2:  uygula → bekle → B ölç → geri al → bekle → A ölç
tur 3:  A ölç → uygula → …
```

Her yazmadan sonra o ayarın kendi **oturma süresi** kadar beklenir; sürücü
değişikliğinden hemen sonraki paketler durumu değil geçişi ölçer.

Bir tur şu durumlarda **atılır ve tekrarlanır**: iki yarı farklı yük altındaysa,
CPU yükü belirgin değiştiyse, güç kaynağı AC↔batarya değiştiyse, Wi-Fi sinyali
veya bağlantı hızı belirgin değiştiyse. Rota, bağdaştırıcı veya erişim noktası
(BSSID) değişirse **tur tamamen iptal edilir** — artık aynı yol ölçülmüyordur.

Sonuç kararsızsa örnek sayısı büyütülür (40 → 120). Hâlâ kararsızsa, ya da süre
sınırına gelinirse, cevap "hayır"dır.

### Kabul kuralı

Bir ayar ancak şunların **hepsi** doğruysa kalır:

- aynı metrikteki kazanç turların çoğunda tekrarlanır;
- hiçbir turda aynı büyüklükte ters sonuç yoktur;
- kazanç, turların birbiriyle olan uyuşmazlığından (robust yayılım) büyüktür;
- yeniden örneklenen (bootstrap) eşli fark aralığının alt sınırı **sıfırı
  dışlar**;
- turların hepsi aynı sırada ölçülmemiştir.

Şunlardan herhangi biri adayı anında bitirir: uzak ucun yanıt vermemesi, paket
kaybının bir probe'dan fazla artması, median / p95 / p99 / jitter'da anlamlı
gerileme, sürücünün değeri canlı uygulamaması, geri alınamayan bir yazma. CPU
maliyeti olan bir ayar (Interrupt Moderation, RSC, LSO) **iki kat** büyük bir
kazanç göstermek zorundadır.

**Tam paket doğrulaması:** adaylar tek tek kabul edildikten sonra tek bir son
ölçüm alınmaz. Kabul edilen ayarların **tamamı**, yine dönüşümlü sırayla,
özgün duruma karşı yeniden ölçülür. Yalnız bu eşli doğrulama da kazanç
gösteriyorsa değişiklikler kalıcı olur; aksi hâlde hepsi geri alınır.

### Yük altındaki gecikme

Ev bağlantılarında en büyük ms kazancı genelde boştaki ping'den değil,
**bir şey gönderirken oluşan kuyruklanmadan** gelir. "Yük altında test et"
şunu yapar:

1. boştaki RTT'yi ölçer;
2. **siz** bir gönderim başlatana kadar bekler ve bağdaştırıcı sayaçlarından
   hattın gerçekten dolduğunu doğrular;
3. o pencerede aynı uca RTT'yi tekrar ölçer;
4. aynısını indirme yönü için yapar;
5. gönderim ve indirme kuyruklanmasını **ayrı ayrı** raporlar.

**Uygulama hiçbir veri göndermez veya indirmez.** Yük sizin başlattığınız
trafiktir; gelmezse cevap "ölçülmedi" olur, tahmin değil. Aynı şekilde, boştaki
ölçüm için hattın gerçekten boşalması gerekir: bir aktarım sürerken test
başlatırsanız çalışma "ölçüm tamamlanmadı" der ve aktarımları durdurup yeniden
denemenizi ister. Bu bir "kazanç yok" sonucu değildir.

"Yüklü" tek bir durum değildir. Kapasite, aktarımınız hızlanıp **plato yaptığında**
öğrenilir (tek bir pencereden değil), yön başına ayrı saklanır, ve üç ayrı durum
raporlanır: *trafik var*, *kapasiteye yaklaştı* (%60) ve *hat doydu* (%85).
Kuyruklanma iddiası **yalnız doygunlukta** üretilir. Kapasite yeterince
ölçülemediyse cevap "ölçülemedi"dir — "bufferbloat yok" değildir.

Derin test bir sihirbazdır ve her aşamada ne beklendiğini söyler: hedef
doğrulama → hattın boşalması → boştaki değer → *gönderimi başlatın* → ölçüm →
*gönderimi durdurun* → ilke uygulama → *yeni bir gönderim başlatın* → ölçüm →
indirme aşaması → doğrulama turu. Anlık hız, kapasiteye yaklaşma yüzdesi, kalan
süre, kullanılan veri ve bir **iptal** düğmesi her aşamada görünür.

### Traffic Guard (isteğe bağlı, varsayılan kapalı)

Gönderim kuyruklanması bulunursa ve siz açıkça açtıysanız, seçtiğiniz **tek bir**
uygulamanın giden trafiği Windows'un kendi Policy-based QoS mekanizmasıyla
sınırlanır, sonra yük altındaki gecikme yeniden ölçülür.

Sınır sabit bir yüzde **değildir**: birkaç aday sınır uygulanıp ölçülür ve
sıralama yük altındaki p95 → p99 → kuyruklanma farkı → jitter → kayıp → korunan
throughput önceliğiyle yapılır. İki mod vardır — *Dengeli* (hızı olabildiğince
korur) ve *En düşük gecikme* (daha fazla hız kaybını kabul eder ve kaybı size
gösterir). Kazanan sınır, **aramada kullanılmayan ayrı bir doğrulama turunda**
yeniden sınanır.

Windows bir QoS ilkesini yalnız **ilke oluşturulduktan sonra açılan** bağlantılara
uygular. Bu yüzden test sırasında aktarımı durdurup yeniden başlatmanız istenir,
ve yeni bir bağlantı görülmeden hiçbir sonuç üretilmez. Sınırlanan, **oyununuz
değil** adını verdiğiniz toplu aktarım uygulamasıdır; uygulama çalışan süreçler
arasından doğrulanır.

- İlke yalnız **`DPIBypass.Latency.`** ön ekiyle oluşturulur; başka hiçbir ad
  oluşturulmaz, değiştirilmez veya silinmez.
- Sizin veya yöneticinizin (GPO) mevcut QoS ilkelerine **dokunulmaz**. Rakip bir
  hız sınırlama ilkesi varsa otomatik müdahale atlanır ve size söylenir.
- İlke `ActiveStore`'da tutulur: yeniden başlatmadan sonra kalmaz.
- Kuyruklanma **ölçülerek** azalmazsa, paket kaybı artarsa veya gönderim hızı
  fazla düşerse ilke silinir.
- İlke oluşturulduktan sonra depodan **her koşul ve eylem** tek tek geri okunur:
  ad, depo, uygulama eşleşmesi, protokol, hedef öneki ve portu, hız sınırı, DSCP
  ve öncelik. Biri tutmuyorsa ilke oluşturulmuş sayılmaz.
- Ölçülen bayt hızı sınırla tutarlı değilse o tur, o sınır hakkında veri
  sayılmaz — ilke depoda görünüyor olsa bile.
- DSCP işaretlemesi tek başına kazanç sayılmaz: router'ın onu sınıflandırıp
  sınıflandırmadığı bu uçtan görülemez.

### Nerede olduğunu söyler

Gecikmenin ne kadarının ilk atlamada, ne kadarının operatör ve internet yolunda,
ne kadarının kendi trafiğinizin ürettiği kuyrukta olduğu ayrıştırılır. İlk atlama
1 ms ve uzak uç 70 ms ise hiçbir bağdaştırıcı ayarı bunu değiştirmez — ve bunu
söylemek sekiz ayarı deneyip bir şey bulamamaktan daha faydalıdır.

**Bunlar aynı şey değildir ve arayüz hiçbirini diğerinin yerine göstermez:**

| Kavram | Nedir | Neyle düşer |
| --- | --- | --- |
| Boştaki ping | Hat boşken gidiş-dönüş süresi | Mesafe ve rota; yerelde çok az şey |
| Yük altındaki gecikme | Siz gönderirken/indirirken RTT | Gönderim hızını sınırlamak (yalnız gönderim yönü) |
| Jitter | Ardışık paketler arası fark | Kararlı hat, kablolu bağlantı |
| Paket kaybı | Yanıtsız probe oranı | Hat/kablo/radyo kalitesi |
| DNS çözümleme süresi | Ad çözme gecikmesi | DNS sağlayıcısı — **RTT değildir** |
| ISP/WAN rota gecikmesi | Operatör ve internet yolu | Bu yazılımla **düşürülemez** |

### Dokunulan ve dokunulmayan

Sürücü gerçekten destekliyorsa denenenler (hepsi standart NDIS registry
keyword'üyle eşleşir, yerelleştirilmiş görünen ad **hiç okunmaz**):

- `*InterruptModeration` → 0
- `*RscIPv4` / `*RscIPv6` → 0 (yalnız TCP hedefinde, yalnız RSC gerçekten
  çalışıyorsa)
- `*RSS` → 1 (yalnız kablolu, ≥4 mantıksal işlemci, şu anda kapalıysa)
- `*EEE` → 0
- `*LsoV2IPv4` / `*LsoV2IPv6` → 0 — **şu anda hiçbir tur tarafından denenmiyor.** LSO
  yalnız toplu gönderimi etkiler ve boştaki gecikme turunda parçalanacak büyük bir
  blok yoktur; yük altındaki lane ise NIC ayarı değil hat ve QoS ölçer. Katalogda
  kalma nedeni, eski bir kayıttan geri yükleyebilmektir.

**Bir ayar "uygulandı" sayılmaz, kanıtlanır.** Microsoft'un kendi belgesi
`-NoRestart` için *"Many advanced properties require restarting the network
adapter before the new settings take effect"* diyor — yani registry'nin yeni
değeri göstermesi sürücünün onunla çalıştığını kanıtlamaz. Değer önce yeniden
başlatmadan yazılır; `Get-NetAdapterRsc` / `Get-NetAdapterRss` /
`Get-NetAdapterLso` ayarın **gerçekten etkin** olduğunu bildirirse ölçüm başlar.
Bildirmiyorsa ve siz kontrollü yeniden başlatmaya izin vermediyseniz aday
"yeniden başlatma gerekiyor" olarak raporlanır ve **ölçülmez**. İzin verdiyseniz
bağdaştırıcı yeniden başlatılır ve aynı bağdaştırıcı, link, IP, gateway, ilk
atlama ve erişim noktası doğrulanmadan hiçbir ölçüm yapılmaz. Uzak masaüstü
oturumunda yeniden başlatma **hiçbir koşulda** yapılmaz.

`SelectiveSuspend`, `D0PacketCoalescing` ve `DeviceSleepOnDisconnect` **artık
aday değildir**. İlki yalnız uzun boşluktan sonraki ilk paketi etkiler ve sürekli
probe gönderen bir deney o durumu hiç üretmez; ikincisi yayın/çoklu yayın
alımlarını birleştirir ve unicast oyun trafiğine mekanizması yoktur; üçüncüsü
bağlantı kesildiğinde ne olduğunu yönetir. Üçü de yalnız eski bir kayıt taşıyan
makineler için **geri yüklenebilir** listede kalır.

**Checksum offload hiçbir koşulda kapatılmaz.** Microsoft bunların her iş
yükünde açık kalmasını önerir ve RSS, RSC, LSO bunlara bağımlıdır.

MTU, TCP autotuning, ECN, Nagle/registry hack'leri, `NetworkThrottlingIndex`,
`SystemResponsiveness`, HPET/timer ayarları, DNS, IPv6, route/metric, firewall,
güvenlik servisleri, güç planı ve işlem önceliği değiştirilmez. Bağdaştırıcı
kapatılıp açılmaz ve yeniden başlatılmaz. VPN, TAP/TUN, Hyper-V, Docker ve WSL
sanal bağdaştırıcıları atlanır. Paket yoluna hiç dokunulmaz: bu özellik tek bir
WinDivert tanıtıcısı açmaz, oyun ve ses trafiği normal Windows ağ yolunda kalır.

Bu özellik ISP rotasını değiştirmez, VPN değildir ve uzaktaki oyun sunucusunu
fiziksel olarak yakınlaştırmaz.

### "Kazanç bulunamadı" kapalı demek değildir

Mod açıkken yerel olarak düzeltilebilir bir şey bulunamazsa arayüz **kapalı
göstermez.** Ayırt edilen durumlar:

| Durum | Ne demek |
| --- | --- |
| `Kapalı` | Anahtar kapalı |
| `Açık · ölçülüyor` | Başlangıç ölçümü sürüyor |
| `Açık · hızlı test yapılıyor` | Ayar değiştirmeden ölçüm |
| `Açık · yük altında derin test yapılıyor` | Sizin trafiğiniz bekleniyor/ölçülüyor |
| `Açık · NIC ayarı medianı X ms, p95'i Y ms azalttı` | Doğrulanmış kazanç uygulandı |
| `Açık · Traffic Guard gönderim kuyruklanmasını X ms azalttı` | Ölçülmüş QoS kazancı |
| `Açık · ağ izleniyor · yerel olarak uygulanabilir kazanç bulunamadı` | Mod çalışıyor, yapılacak bir şey yok |
| `Açık · gecikmenin X ms'i ISP/WAN rotasında; yerel ayar bunu değiştiremez` | Fiziksel sınır |
| `Derin test gerekli · yalnız boşta bağlantı ölçüldü` | Yük altındaki tablo bilinmiyor |
| `Açık · müdahale geri alındı · <neden>` | Bir aday denendi ve geri alındı |
| `Desteklenen NIC adayı yok · hedef ve yük tanılaması kullanılabilir` | Sürücü hiçbir güvenli ayar sunmuyor |
| `Geri yükleme bekliyor` / `Başarısız` | Kurtarma gerekiyor; yeni ölçüm başlatılmaz |

### Profil ve kurtarma

Doğrulanan sonuç **ağ + bağdaştırıcı + sürücü yetenek parmak izi + ölçüm hedefi
+ ölçüm yöntemi sürümü** anahtarıyla `latency-profiles.json` içinde saklanır.
Aynı ağa dönüldüğünde tam ölçüm yerine kayıtlı ayarlar yeniden uygulanır ve
taze bir doğrulama ölçümüyle onaylanır; onaylanmazsa geri alınır ve profil
silinir.

**Elemeler ile kabuller aynı güven seviyesinde tutulmaz.** Bir kabul her
tekrarında yeniden kanıtlanır; bir eleme ise yalnız sessizce bir adayı ölçüm
dışı bırakır. Bu yüzden elemeler **3 gün** sonra, kabuller **30 gün** sonra
geçersizdir — ve bir eleme yalnız ölçüldüğü koşullar hâlâ geçerliyken sayılır:
hedef, güç kaynağı, erişim noktası, sinyal seviyesi, "yük altında ölçüldü mü" veya
bağdaştırıcı yeniden başlatma izniniz değişirse aday yeniden ölçülür.
Ayrıntılar bölümündeki **"Zorla yeniden ölç"** düğmesi ve `latency retest`
komutu önbelleği tamamen atlar.

**Yalnız gerçekten ölçülmüş bir sonuç eleme sayılır.** Hiç denenemeyen bir
aday — yeniden başlatma izni verilmediği için atlanan, sürücünün desteklemediği,
süre sınırına takılan, siz durdurduğunuz veya ağ değiştiği için yarım kalan —
"faydasız" olarak kaydedilmez ve sonraki çalışmada atlanmaz. Özellikle:
**yeniden başlatma iznini sonradan verdiğinizde, o izin yüzünden atlanmış
ayarlar ilk fırsatta ölçülür.** Bu ayrımı yapmayan sürümlerin bıraktığı kayıtlar
ölçüm yöntemi sürümüyle geçersiz sayılır; bu tek bir yeniden ölçüme mal olur.

Dosyada adres, SSID, BSSID veya tam işlem yolu tutulmaz; ağ, erişim noktası ve
hedef kısa hash'lerle temsil edilir ve hiçbir yere gönderilmez.

Özgün değerler `C:\ProgramData\DPI Bypass\latency-snapshot.json` içinde,
her adımdan **önce** yazılan bir durum damgasıyla tutulur
(`SnapshotCreated → CandidateApplied → Verifying → Committed`). Dosya artık
yalnız NIC özelliklerini değil, bu uygulamanın oluşturduğu QoS ilkelerini de
taşır. Mod kapatıldığında, ağ değiştiğinde, uygulama normal kapandığında ve
kaldırma başlamadan önce geri yüklenir. Yarım kalmış bir çalışma — crash,
elektrik kesintisi, süreç sonlandırma — sonraki açılışta **modun açık olup
olmadığına bakılmaksızın** geri alınır. Geri alınamayan bir kaynak, geri
alınabilenleri engellemez; çözülemeyen kayıtta korunur ve yeni bir optimizasyon
başlatılmaz.

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
DpiBypass.exe vodafone on / off   # Vodafone Sınırsız Modu'nu aç / kapat
DpiBypass.exe vodafone diagnose   # Vodafone bağlantısını incele
DpiBypass.exe hotspot diagnose    # mobil paylaşım bağlantısını incele
DpiBypass.exe hotspot cleanup     # eski alan adlarını güncel ayarlara taşı
DpiBypass.exe latency status                 # düşük-gecikme durumu
DpiBypass.exe latency status --json          # otomasyon için kararlı şema
DpiBypass.exe latency on / off               # ölçümlü optimizasyonu aç / kapat
DpiBypass.exe latency test --target mc.sunucu.com:25565
                                             # kalıcı ayar değiştirmeden ölç
DpiBypass.exe latency target mc.sunucu.com:25565
                                             # ölçüm hedefini kalıcı ayarla
DpiBypass.exe latency optimize --quick       # hızlı ölçüm
DpiBypass.exe latency optimize --deep        # yük altında derin test
DpiBypass.exe latency loaded-test            # yük altında derin test
DpiBypass.exe latency retest                 # kayıtlı sonucu yok say, baştan ölç
DpiBypass.exe latency report                 # son tam raporu yazdır
DpiBypass.exe latency profiles clear         # kayıtlı per-ağ sonuçlarını sil
DpiBypass.exe latency restore                # özgün NIC değerlerini kurtar
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
her açılışta yönetici onayı sorulmaz. Görev kaydedilemezse uygulama doğrudan
`Run` anahtarına düşer — o durumda onay istenir. İstemiyorsanız Windows Başlangıç
Uygulamaları'ndan veya **DNS ve ayarlar** sekmesinden kapatabilirsiniz.

**Görev artık kendi oturum açma tetikleyicisine sahiptir.** Önceden tek başlatma
yolu `Run` kaydıydı: o kayıt herhangi bir sebeple kaybolduğunda (temizlik aracı,
profilin yeniden oluşturulması, kurulumun başka bir yönetici hesabıyla
yükseltilmesi) uygulama bir daha hiç açılmıyordu ve bunu söyleyen hiçbir şey
yoktu. Şimdi görev oturum açıldıktan 10 saniye sonra kendiliğinden çalışır;
`Run` kaydı yine yazılır, çünkü Windows Ayarları'ndaki anahtarı o gösterir. İki
yol da aynı görevi çalıştırdığı için ikinci kopya oluşmaz.

**Windows Ayarları'ndaki anahtar hâlâ belirleyicidir.** Oturum açma tetikleyicisi
uygulamayı `--autostart` ile başlatır; bu şekilde başlayan bir kopya, Windows
kaydı "kapalı" olarak işaretlemişse hiçbir şey yapmadan çıkar. Elle açılan bir
kopya bundan etkilenmez.

Ayar açıkken kayıt eksik ya da eski biçimdeyse (tetikleyicisi olmayan bir görev,
silinmiş bir görev) uygulama **her açılışta onu yeniden kurar**. Anahtarı Windows
Ayarları'ndan kapattıysanız bu onarım çalışmaz; o karar sizindir ve olduğu gibi
kalır.

## Ayarlar

`C:\ProgramData\DPI Bypass\`

| Dosya | İçerik |
| --- | --- |
| `settings.json` | Kapsam, DNS kipi, yöntem seçimi, Ping düşürme, Vodafone ağ tercihleri ve hotspot tanılaması, başlangıç seçenekleri |
| `networks.json` | Ağ başına öğrenilen yöntem belleği |
| `learned-domains.json` | Otomatik keşfin bulduğu engelli alan adları |
| `dns-snapshot.json` | Değiştirilmeden önceki DNS ayarlarınız |
| `latency-snapshot.json` | Ping düşürmenin değiştirdiği NIC özelliklerinin tam özgün değerleri ve işlem durumu |
| `latency-profiles.json` | Ağ + bağdaştırıcı + sürücü + hedef başına doğrulanmış ölçüm sonuçları (yalnız yerel, hash'li) |
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
  Network/LatencyStatistics.cs median, p95, p99, jitter, kayıp, bootstrap aralığı
  Network/LatencyProbe.cs     sıralı, ısınmalı ve sabit aralıklı RTT ölçümü
  Network/LatencyProfileStore.cs ağ + bağdaştırıcı + sürücü + hedef başına sonuç
  Network/Latency/            hedef çözümleme, müdahale kataloğu, deney koşucusu,
                              ortam örnekleyici, yük deneyi, QoS ve Traffic Guard
  Network/NetworkLoadSampler.cs ölçüm penceresinde hattın ne kadar meşgul olduğu
  Diagnostics/StrategyTuner.cs        gerçek bağlantı testleriyle yöntem arama
  Diagnostics/BlockedSiteDiscovery.cs yeni engelli siteleri ölçerek bulma
  MobileHotspot/MobileHotspotDiagnostics.cs salt-okunur bağlantı incelemesi
  MobileHotspot/HotspotLegacyMigration.cs eski alan adlarını güncel ayarlara taşıma
  Vodafone/HotspotTtlFix.cs               tek bağdaştırıcıda giden TTL'yi yeniden yazma
  Vodafone/TtlFixSettings.cs              TTL, koruma eşiği ve IPv6 seçeneği
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
| Windows açılışında uygulama hiç başlamıyor | Başlatmanın tamamı tek bir `Run` kaydına bağlıydı; o kayıt silindiğinde açılışta hiçbir şey olmuyordu. Görev artık kendi oturum açma tetikleyicisiyle çalışır ve ayar açıkken eksik kayıt her açılışta yeniden kurulur. Sürümü güncelleyip **DNS ve ayarlar** sekmesinden "Windows ile başlat"ı bir kez kapatıp açın; günlükte "Autostart registration is incomplete" satırı onarımın yapıldığını söyler. Windows Ayarları → Uygulamalar → Başlangıç'ta anahtar kapalıysa uygulama bilerek başlamaz |
| Durum uzun süre "Başlatılıyor…" kalıyor | Her ağ bağdaştırıcısının DNS değişikliği ayrı bir `powershell.exe` idi; birkaç bağdaştırıcısı olan makinede bu tek başına bir dakikaya yaklaşıyordu. Artık hepsi tek çağrıda uygulanır ve durum satırı hangi adımda olduğunu yazar ("Ad çözümleme ayarlanıyor…", "Ağ sürücüsü açılıyor…"). Yine uzun sürüyorsa o günün günlüğündeki `DNS set to` satırının zaman damgasını bildirin |
| Fare tekerleği sayfayı kaydırmıyor / açılır liste kendiliğinden değişiyor | Liste veya açılır kutu üzerindeyken tekerlek sayfaya ulaşmıyordu, açılır kutular ise tekerleği seçim değiştirmek için kullanıyordu. Artık liste sonuna geldiğinde tekerlek sayfaya devredilir ve açılır kutu, listesi açık değilken tekerleğe hiç dokunmaz |
| "Başka bir kullanıcı oturumunda çalışıyor" | Koruma bilgisayar başına tek kopyadır. Diğer Windows oturumunda açık olan kopyayı kapatın |
| "Yönetici hakları gerekiyor" | Uygulamayı yönetici olarak çalıştırın; sürücü aksi hâlde açılamaz |
| Durum "engel sürüyor" diyor | **Ağ ve yöntem** → *Yeniden tara*. Çalışan bulunmazsa DNS modunu veya kapsamı değiştirip yeniden deneyin |
| Tarayıcıda açılmıyor, uygulamada açılıyor | Kapsamı **Engelli siteler + tarayıcılar** yapın ve QUIC engellemesini açık bırakın |
| DNS bozuk kaldı | Uygulamayı bir kez çalıştırıp kapatın; `DpiBypass.exe restore-dns` de ayarları geri yükler |
| Telefon paylaşımında bazı sayfalar yarım yükleniyor | **DNS ve ayarlar → Vodafone Sınırsız Modu** → *Tanıla*. 1500 baytlık paketler geçmiyorsa rapor ölçülen parçalanmasız sınırı söyler; yalnızca belirti varsa bu sınıra yakın bir MTU denenip yeniden doğrulanmalıdır |
| Vodafone Sınırsız Modu kayıtlı ağımı tanımıyor | İki sebebi vardı ve ikisi de giderildi: ağ kimliği yalnız koruma çalışırken okunuyordu, ve eşleştirme erişim noktasının MAC adresini içeren parmak izine bakıyordu — telefon paylaşımı her açılışta yeni bir rastgele MAC dağıttığı için kayıt tanınmıyordu. Artık ağ adı da eşleştirilir, kayıt bu oturumun kimliğiyle güncellenir ve kart kayıtlı ağda "Aktif · \<ağ adı\>" der. Hâlâ tanımıyorsa **"Bu ağı kaydet"** ile bir kez kaydedin |
| Vodafone Sınırsız Modu açık ama bir şey değişmiyor | Windows'ta modun "açık" olması yetmez; kartta **"Aktif · \<ağ adı\> · TTL 65"** yazmalı ve düzeltilen paket sayacı artmalıdır. "Kurulamadı" diyorsa sebebi hemen yanında yazar: uygulamayı **yönetici olarak** çalıştırın ve kurulum klasöründeki WinDivert dosyalarının yerinde olduğunu doğrulayın. Ağ kayıtlı değilse **"Bu ağı kaydet"** deyin |
| Linux'ta çalışıyor, Windows'ta çalışmıyordu | Bir ara sürüm Windows tarafında TTL yeniden yazımını tamamen kaldırmış, anahtarı yalnız salt-okunur tanılamaya bağlamıştı. Mekanizma geri getirildi; iki sürüm de aynı TTL (65) ve aynı koruma eşiği (32) ile çalışır |
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
