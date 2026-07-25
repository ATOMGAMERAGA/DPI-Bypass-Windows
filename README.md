<div align="center">

<img src="assets/logo/atomdpi-256.png" width="128" alt="Atom DPI Bypass" />

# Atom DPI Bypass

**Türkiye'deki DPI (derin paket denetimi) engellerini aşan Windows uygulaması.**

Discord başta olmak üzere HTTPS/HTTP bağlantılarını, ping ve ses trafiğine
dokunmadan çalışır hâle getirir.

Geliştirici: **Atom Gamer Arda A.G.A**

</div>

---

## Tek satırla kurulum

PowerShell'i açın ve şunu yapıştırın:

```powershell
irm https://raw.githubusercontent.com/ATOMGAMERAGA/DPI-Bypass-Windows/main/scripts/install.ps1 | iex
```

Betik son sürümü indirir, yayınlanan SHA256 listesiyle doğrular ve sessizce
kurar. Yönetici hakkı gerekiyorsa kendisi yükseltilmiş bir pencere açar.

Kurulum dosyasını elle indirmeyi tercih ederseniz
[Releases](../../releases/latest) sayfasındaki
`AtomDpiBypass-Setup-<sürüm>.exe` dosyasını çalıştırın. Kurulum istemeyenler için
aynı sayfada `AtomDpiBypass-Portable-<sürüm>.zip` de bulunur.

## Ne yapıyor?

Türkiye'de Discord gibi servisler DNS seviyesinde **ve** DPI seviyesinde
engellenir: TLS el sıkışmasının ilk paketindeki alan adı (SNI) okunur ve
bağlantı ya sıfırlanır ya da sessizce düşürülür. Atom DPI Bypass bu iki katmanı
birlikte ele alır.

**1. Paket katmanı.** Bağlantının yalnızca ilk veri paketi (TLS ClientHello ya
da düz metin HTTP istek başlığı) çekirdek süzgeciyle yakalanır ve denetleyicinin
akışı birleştirememesi için yeniden şekillendirilir:

| Yöntem | Ne yapar |
| --- | --- |
| Bölme | Alan adının tam ortasından iki TCP parçasına ayırır |
| Ters sıralı bölme | Parçaları ters sırada gönderir |
| Sahte paket (düşük TTL) | Denetleyiciye zararsız bir el sıkışma gösterir; paket sunucuya varmadan TTL ile ölür |
| Sahte paket (geçersiz sıra no) | Sunucunun pencere dışı sayıp attığı bir kopya gönderir |
| Sahte paket (bozuk sağlama) | Sunucunun sağlama hatası nedeniyle attığı bir kopya gönderir |
| Üç parçalı bölme | İki noktadan keser |
| Bant dışı bayt | URG bayrağıyla tek bir bayt önden gönderir |
| HTTP başlık oyunları | `Host:` başlığının yazımını değiştirir |

Bu yöntemler kombinasyonlarıyla birlikte 14 hazır tarif oluşturur.

**2. Alan adı katmanı.** Sorgular DNS-over-HTTPS ile taşınır: **Cloudflare
birincil, Google ve Quad9 yedek**. Çözümleyicilere IP adresiyle bağlanıldığı
için TLS içinde alan adı hiç gönderilmez — dolayısıyla DNS trafiğinin kendisi
alan adına göre süzülemez. Yanıtlar yerel olarak önbelleğe alınır.

## Kendi kendine ayar bulması

Uygulama hangi yöntemin çalıştığını varsaymaz, **ölçer**:

1. Bulunduğunuz ağın kimliği çıkarılır (Wi-Fi adı, ağ geçidinin MAC adresi,
   bağlantı türü).
2. Operatör otomatik algılanır (ters DNS, ASN ve ağ adı ipuçlarıyla) ve o
   operatöre uygun yöntem sıralaması seçilir.
3. Önce hiç dokunmadan denenir — ağ zaten engellemiyorsa hiçbir şey yapılmaz.
4. Aksi hâlde adaylar tek tek uygulanır ve her biri için **gerçek bir
   discord.com TLS el sıkışması** yapılır. Sertifika da doğrulanır, böylece
   araya giren bir kutu "başarılı" sayılmaz.
5. Çalışanlar arasından **en hızlısı** seçilir ve o ağ için hatırlanır.

**Ağ değiştiğinde** (örneğin `atom` adlı ağdan `atoms hotspot` adlı ağa
geçtiğinizde) bu arka planda kendiliğinden yeniden çalışır. O ağ daha önce
görüldüyse kayıtlı yöntem önce denenir; hâlâ çalışıyorsa saniyeler içinde hazır
olur, çalışmıyorsa yeni bir arama başlar.

Desteklenen operatör profilleri: Türk Telekom (Mobil / Evde İnternet / Hotspot),
Redbox, Turkcell (Mobil / Superonline / Superbox / Hotspot), Vodafone (Mobil /
Evde İnternet / Hotspot), TurkNet ve "Diğer / Bilinmiyor".

## Neyin korunacağını siz seçiyorsunuz

Arayüzdeki **Kapsam** sekmesinde üç seçenek var:

- **Yalnızca Discord** — sadece Discord uygulamasının trafiği ve Discord alan
  adları. Sisteme etkisi en düşük seçenek. Kurulu Discord sürümleri (kararlı,
  PTB, Canary, Microsoft Store) otomatik algılanır ve trafik, paketi açan
  sürecin kimliğine göre eşleştirilir.
- **Discord + tarayıcılar** — buna ek olarak kurulu tarayıcılardaki tüm siteler.
- **Tüm sistem** — bilgisayardaki bütün programlar.

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
- Otomatik ayarlama, çalışan yöntemler arasından en düşük gecikmeliyi seçer.

## Ekran arayüzü

Windows 11'in Fluent görünümünü ve Mica malzemesini kullanır, sistem
açık/koyu temasını canlı olarak izler. Logo 16 pikselden 256 piksele kadar her
boyutta ayrı ayrı gömülüdür ve arayüzde 1024 piksellik kaynaktan çizilir;
böylece tepside, görev çubuğunda, kurulum sihirbazında ve %350 ölçeklemede
bulanıklaşmaz.

Sekmeler: **Durum** (durum, aç/kapat, discord.com testi, sayaçlar), **Kapsam**,
**Ağ ve yöntem**, **DNS ve ayarlar**, **Günlük**.

## Gereksinimler

- Windows 10 sürüm 1809 veya daha yenisi / Windows 11, 64 bit
- Yönetici hakları (ağ sürücüsü açmak için zorunlu)
- .NET kurmanız gerekmez; uygulama çalışma zamanını kendi içinde taşır

## Otomatik başlatma

Kurulumda, oturum açıldığında **yükseltilmiş** çalışan bir Görev Zamanlayıcı
görevi kaydedilir (`AtomDpiBypass-Autostart`). Bu sayede her açılışta yönetici
onayı sorulmaz. Görev kaydedilemezse `Run` anahtarına düşülür — o durumda onay
istenir. İstemiyorsanız **DNS ve ayarlar** sekmesinden kapatabilirsiniz.

## Kaldırma

Ayarlar → Uygulamalar üzerinden normal şekilde kaldırılır. Kaldırma sırasında
özgün DNS ayarlarınız geri yüklenir, oturum açma görevi silinir ve WinDivert
sürücü servisi kaldırılır.

## Kaynaktan derleme

```powershell
git clone https://github.com/ATOMGAMERAGA/DPI-Bypass-Windows.git
cd DPI-Bypass-Windows

./tools/fetch-windivert.ps1                        # sürücü dosyalarını indirir
dotnet test tests/AtomDpi.Tests/AtomDpi.Tests.csproj
dotnet publish src/AtomDpi.App/AtomDpi.App.csproj -c Release -o artifacts/publish
```

Kurulum paketi için Inno Setup 6 gerekir:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0.0 /DPublishDir=..\artifacts\publish installer\AtomDpiBypass.iss
```

Logo dosyalarını yeniden üretmek için (Python + Pillow):

```bash
python3 tools/generate_assets.py assets/logo/source.png
```

### Proje yapısı

| Yol | İçerik |
| --- | --- |
| `src/AtomDpi.Core` | Paket motoru, DNS, operatör profilleri, otomatik ayarlama |
| `src/AtomDpi.App` | WPF arayüzü, tepsi simgesi, otomatik başlatma |
| `tests/AtomDpi.Tests` | Birim testleri (129 test) |
| `installer/` | Inno Setup betiği ve sihirbaz görselleri |
| `tools/` | Sürücü indirme ve logo üretme betikleri |
| `.github/workflows/` | Derleme, test ve sürüm yayınlama hattı |

### Sürümleme

`Directory.Build.props` içindeki `VersionPrefix` ana sürümü belirler; `main`
dalına yapılan her birleştirme, sonuna CI çalışma numarası eklenmiş yeni bir
sürüm (`1.0.0.42` gibi) olarak otomatik yayınlanır.

## Sorun giderme

| Belirti | Bakılacak yer |
| --- | --- |
| "Yönetici hakları gerekiyor" | Uygulamayı yönetici olarak çalıştırın; sürücü aksi hâlde açılamaz |
| Durum "engel sürüyor" diyor | **Ağ ve yöntem** → *Yeniden tara*. Çalışan bulunmazsa DNS modunu veya kapsamı değiştirip yeniden deneyin |
| Tarayıcıda açılmıyor, uygulamada açılıyor | Kapsamı **Discord + tarayıcılar** yapın ve QUIC engellemesini açık bırakın |
| DNS bozuk kaldı | Uygulamayı bir kez çalıştırıp kapatın; `AtomDpiBypass.exe --restore-dns` de ayarları geri yükler |
| Günlükler | **Günlük** sekmesi → *Klasörü aç* (`C:\ProgramData\Atom DPI Bypass\logs`) |

## Yasal not

Bu araç, kullanıcının kendi internet bağlantısı üzerinde hangi paketlerin nasıl
biçimlendirileceğini belirlemesini sağlar; başkalarının sistemlerine erişmek,
kimlik doğrulamayı atlatmak veya trafiği izlemek için bir mekanizma içermez.
Bulunduğunuz yerdeki mevzuata uymak kullanıcının sorumluluğundadır.

## Lisans

Bu depodaki kod [LICENSE](LICENSE) dosyasındaki koşullara tabidir. Birlikte
dağıtılan üçüncü taraf bileşenler ve lisansları
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasında listelenmiştir.
