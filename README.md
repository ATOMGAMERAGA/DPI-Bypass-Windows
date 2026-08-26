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

## Vodafone sınırsız modu (hotspot TTL düzeltmesi)

Vodafone'un "Red Sınırsız" tarifelerinde mobil veri sınırsızdır, ancak
**hotspot/tethering ayrı bir kotadan düşer**. Operatör paylaşımı paketin **TTL**
değerinden anlar: telefonun kendi trafiği operatöre `64` ile ulaşır, laptoptan
gelen paket ise telefonda bir kez yönlendirildiği için `63` olarak varır.

Bu mod, bu bilgisayardan çıkan paketleri **TTL 65** ile yollar. Telefon bir
düşürünce operatöre tam `64` gider.

**Nasıl açılır:** DNS ve ayarlar → *Vodafone sınırsız modu*. Komut satırından:

```powershell
DpiBypass.exe vodafone status
DpiBypass.exe vodafone on
DpiBypass.exe vodafone off
```

**Yalnızca kaydedildiği ağda çalışır.** Modu açtığınız andaki ağın parmak izi
kaydedilir; ev Wi-Fi'ına ya da Ethernet'e geçtiğinizde kural kendiliğinden
kalkar, telefona döndüğünüzde geri gelir. Kural tek bir ağ bağdaştırıcısına
bağlıdır.

**Atlatma bozulmaz.** Sahte paket stratejileri kasıtlı olarak düşük TTL'li
(3-8) paketler gönderir; bu paketlerin sunucuya *ulaşmaması* atlatmanın çalışma
ilkesidir. Bu yüzden TTL yeniden yazımı yalnızca TTL'i **32'nin üstünde** olan
paketlere uygulanır — hem çekirdek süzgecinde hem de kodda. Eşik bir testle
(`TheGuardSitsAboveEveryDecoyTtlInTheLibrary`) korunur: ileride daha yüksek
TTL'li bir strateji eklenirse derleme kırılır.

**IPv6.** Telefon tethering yaparken bilgisayara kendi global IPv6 adresini
verir; o zaman operatör aynı aboneden iki farklı kaynak görür ve TTL ne olursa
olsun paylaşım anlaşılır. Bu yüzden mod, varsayılan olarak paylaşılan
bağdaştırıcıda giden IPv6 trafiğini engeller. Kural kalktığında bu da anında
kalkar — sistemde kalıcı bir değişiklik yapılmaz.

> **Kullanım koşulları uyarısı:** Bu mod, operatör sözleşmenizin kullanım
> koşullarına aykırıdır. Otomatik sayacı atlatır, ancak çok yüksek kullanım
> adil kullanım incelemesine takılabilir — orada TTL'in bir etkisi olmaz.
> Sorumluluk kullanıcıya aittir.

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
DpiBypass.exe vodafone [on|off]   # hotspot TTL düzeltmesi
DpiBypass.exe restore-dns         # DNS ayarlarını geri yükle
```

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

Kurulumda, oturum açıldığında **yükseltilmiş** çalışan bir Görev Zamanlayıcı
görevi kaydedilir (`DpiBypass-Autostart`). Bu sayede her açılışta yönetici
onayı sorulmaz. Görev kaydedilemezse `Run` anahtarına düşülür — o durumda onay
istenir. İstemiyorsanız **DNS ve ayarlar** sekmesinden kapatabilirsiniz.

## Ayarlar

`C:\ProgramData\DPI Bypass\`

| Dosya | İçerik |
| --- | --- |
| `settings.json` | Kapsam, DNS kipi, yöntem seçimi, Vodafone modu, başlangıç seçenekleri |
| `networks.json` | Ağ başına öğrenilen yöntem belleği |
| `learned-domains.json` | Otomatik keşfin bulduğu engelli alan adları |
| `dns-snapshot.json` | Değiştirilmeden önceki DNS ayarlarınız |
| `logs\` | Günlük kayıtları (14 gün saklanır) |

## Kaldırma

Ayarlar → Uygulamalar üzerinden normal şekilde kaldırılır. Kaldırma sırasında
özgün DNS ayarlarınız geri yüklenir, oturum açma görevi silinir ve WinDivert
sürücü servisi kaldırılır.

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
| `src/DpiBypass.Core` | Paket motoru, DNS, operatör profilleri, otomatik ayarlama, TTL düzeltmesi, denetim kanalı |
| `src/DpiBypass.App` | WPF arayüzü, tepsi simgesi, otomatik başlatma, komut satırı |
| `tests/DpiBypass.Tests` | Birim testleri (184 test) |
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
  Diagnostics/StrategyTuner.cs        gerçek bağlantı testleriyle yöntem arama
  Diagnostics/BlockedSiteDiscovery.cs yeni engelli siteleri ölçerek bulma
  Vodafone/HotspotTtlFix.cs   hotspot TTL düzeltmesi (eşik korumalı)
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
| Uygulama çalışıyor ama hiçbir yerde görünmüyor | Bildirim alanı simgesi **^** okunun altında olabilir. `DpiBypass.exe --show` pencereyi her koşulda açar |
| Kurulumdan hemen sonra "yol bulunamadı" gibi bir hata çıkıyor | Kurulum, uygulamayı silmek üzere olduğu geçici klasörden başlatıyordu; uygulama artık her başlangıçta kendi klasörüne geçiyor ve yardımcı programları tam yolla çağırıyor. Güncel sürümde görülmemeli — görülüyorsa o günün günlüğündeki ilk iki satırı (sürüm, klasör, komut satırı) bildirin |
| Pencere açılıyor ama içi boş / saydam görünüyor | Windows'ta *Ayarlar → Kişiselleştirme → Renkler → Saydamlık efektleri* kapalıysa ya da donanım hızlandırma yoksa uygulama düz renkli arka plana kendiliğinden geçer — pencere zaten açıkken de denetlenir. Geçmediyse günlükteki "Pencere arka planı" satırını bildirin |
| Pencere geç geliyor / bir süre boş duruyor | Çalışan kopya meşgulse elle açılan kısayol artık onu kapatmak yerine bekliyor, ve motor günlükleri arayüzü kilitlemeyecek şekilde toplu işleniyor. Sorun sürüyorsa günlükteki "Çalışan kopya meşgul" satırına bakın |
| "Başka bir kullanıcı oturumunda çalışıyor" | Koruma bilgisayar başına tek kopyadır. Diğer Windows oturumunda açık olan kopyayı kapatın |
| "Yönetici hakları gerekiyor" | Uygulamayı yönetici olarak çalıştırın; sürücü aksi hâlde açılamaz |
| Durum "engel sürüyor" diyor | **Ağ ve yöntem** → *Yeniden tara*. Çalışan bulunmazsa DNS modunu veya kapsamı değiştirip yeniden deneyin |
| Tarayıcıda açılmıyor, uygulamada açılıyor | Kapsamı **Engelli siteler + tarayıcılar** yapın ve QUIC engellemesini açık bırakın |
| DNS bozuk kaldı | Uygulamayı bir kez çalıştırıp kapatın; `DpiBypass.exe restore-dns` de ayarları geri yükler |
| Vodafone modu "kayıtlı değil" diyor | Mod yalnızca açtığınız ağlarda çalışır. Telefonun paylaşımına bağlıyken onay kutusunu yeniden işaretleyin |
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
