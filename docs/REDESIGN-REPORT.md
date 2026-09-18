# Arayüz, pencere malzemesi, karşılama ve ölçüme dayalı profil seçimi

Başlangıç noktası: `adcb136` (dal: `claude/elegant-ptolemy-bhujwk`).
Bu belge yapılan işin kaydıdır, işin yerine geçmez. Her başlık altında **ne
değişti**, **hangi kanıtla** ve **neyin doğrulanamadığı** ayrı ayrı yazılıdır.

Bu geçiş **Linux üzerinde** yapıldı. .NET 10 SDK ile hem derleme hem de birim
testlerinin tamamı çalıştırıldı (WPF projesi `EnableWindowsTargeting` ile
Linux'ta derleniyor), ancak **uygulama hiç çalıştırılmadı**: Windows makinesi,
WinDivert sürücüsü ve ekran yoktu. Bunun sonuçları her başlıkta ve son bölümde
açıkça yazılıdır.

---

## 0. Kapılar

| Kapı | Referans (`adcb136`) | Bu dalda |
| --- | --- | --- |
| `dotnet build DpiBypass.slnx -c Release` | başarılı | başarılı, 0 uyarı |
| `dotnet test` | 1155 başarılı / 0 başarısız | **1268 başarılı / 0 başarısız** |
| `dotnet publish … -o artifacts/publish` | — | başarılı; `DpiBypass.exe` + `DpiBypass.Recovery.exe` üretildi (self-contained win-x64, 258 dosya) |
| `scripts/tests/xaml-resources.tests.ps1` | başarılı | **çalıştırılamadı** (PowerShell yok); betiğin mantığı Python'da birebir yeniden uygulanarak çalıştırıldı: 289 anahtar, 596 başvuru, eksik/ileri başvuru yok |
| `--ui-selftest` (gerçek WPF penceresi) | CI'da çalışır | **çalıştırılamadı** (Windows yok); kapsamı genişletildi, aşağıya bakınız |

Eklenen test sayısı: **113**.

---

## 1. Simgeler: elle çizimden Fluent System Icons'a *(uygulandı, statik olarak doğrulandı)*

**Bulgu.** `Theme/Shared.xaml` kendi simgelerini çiziyordu: 24 birimlik bir
ızgara üzerinde 1.6 kalınlıkta, Microsoft Fluent System Icons ailesindeki
şekillere benzetilmiş 11 adet `Path` geometrisi. Üç sorunu vardı:

- Fluent iki ağırlık çizer (Regular/Filled); bunlar tek ağırlıktı, dolayısıyla
  gezinme çubuğunda **seçili sayfanın simgesi seçili olmayandan farksızdı**.
- Tek bir çizim 12, 14, 16, 18 ve 22 DIP'te ölçeklenerek kullanılıyordu. 24
  birimlik bir çizimin %58'i olan 14 DIP'lik arama simgesi, Windows'un geri
  kalanı keskinken ince ve yumuşak kalıyordu.
- Geometriler elle yazıldığı için üst kaynakla karşılaştırılamıyordu.

**Değişiklik.** `tools/fetch-fluent-icons.py`, resmî SVG varlıklarını
`microsoft/fluentui-system-icons` deposundan çeker ve
`src/DpiBypass.App/Theme/Icons.xaml` dosyasını üretir. 46 simge, her biri **iki
boyutta** (20 ve 24 piksel) ve uygun olanlarda iki varyantta: toplam 132
geometri. Yol verisi değiştirilmez; yalnız `F1` (nonzero) dolgu kuralı ön eki
eklenir, çünkü SVG'nin varsayılanı nonzero, WPF'nin yol dilbilgisininki
evenodd'dur ve ön ek olmadan alt yolları aynı yönde dönen her simge delik delik
çizilirdi.

`Infrastructure/FluentIcon.cs` boyutu geometriye çevirir: 16-20 DIP için 20
piksellik çizim, üstü için 24 piksellik çizim. Ölçeklenen **simgenin tasarım
ızgarasıdır**, yol sınırları değil — yol sınırlarını kutuya yaymak, ızgarasının
üçte birini dolduran chevron'u neredeyse tamamını dolduran kalkanla aynı
boyutta gösterir ve ailenin optik ritmini bozardı.

Ayrıca `Theme/Tokens.xaml`: 4/8 DIP boşluk ritmi, köşe yarıçapları, tipografi
ölçeği, kenarlık kalınlıkları, simge boyutları ve hareket süreleri/easing'leri.
Stiller bunları `StaticResource` ile okur ve kendi sayısını yazmaz. Renk iki
palette kalır; iki palet arasında değişmesi gereken tek eksen odur.

**Kanıt.** `DesignSystemTests` (15 test):

- Arayüzün adını verdiği her simge, **her iki ızgarada** da var.
- Üretilen her geometri `F1 M` ile başlıyor — bu, klavyeden değil bir SVG'den
  geldiğinin kanıtı.
- `MainWindow.xaml` ve `Shared.xaml` içinde `Stroke` taşıyan tek bir `Path` ya
  da tek bir `Geometry` tanımı kalmadı; emoji de yok.
- İki palet aynı anahtar kümesini tanımlıyor.
- Hiçbir stil ya da sayfa kendi rengini (`#RRGGBB`) yazmıyor.
- Her hareket süresi ait olduğu banda düşüyor (mikro 100-160 ms, içerik
  180-240 ms, durum 250-350 ms).
- **Her `StaticResource` başvurusu var olan bir anahtarı adlandırıyor** ve
  `Shared.xaml` bağımlılıklarını kullanmadan önce merge ediyor. Bu, derlemenin
  yakalayamadığı tek ölümcül kaynak hatasıdır: çözülemeyen bir
  `StaticResource`, pencere kurulurken fırlatır ve uygulama açılıp penceresiz
  kalır.

`FluentIcon` bir `ControlTemplate` içindeki `Path` değil, kendi kendini çizen bir
`FrameworkElement`'tir. Bu bir tercih değil zorunluluktur: `Stretch="None"` olan
bir `Path`, istenen boyutu olarak geometrisinin sınırlarını bildirir ve WPF,
istediğinden küçük bir alana yerleştirilen her öğeye bir yerleşim kırpması
uygular. 20 birimlik bir çizimi 16 DIP'lik bir kutuya koymak, ölçek dönüşümü
çalışmadan **önce** simgenin sağ ve alt beşte birini keserdi — ve bu penceredeki
her düğme simgesi 16 DIP'tir. `MeasureOverride` istenen boyutu döndürüp ölçek
`OnRender` içinde uygulandığında kırpılacak bir yerleşim yoktur; maliyeti tek
bir geometri çizimidir. Bir test, stilin yeniden `Template` kazanmadığını
doğruluyor.

Lisans bildirimi `THIRD-PARTY-NOTICES.md` içine eklendi (MIT).

**Doğrulanamayan.** Simgelerin gerçek ekranda optik hizalaması ve %100/125/150/
200 ölçeklerdeki keskinliği. Vektör oldukları ve yerel ızgaralarında
çizildikleri için beklenti iyidir, ancak bu bir beklentidir; ölçüm değildir.

---

## 2. Pencere malzemesi: Mica'nın neden görünmediği *(kanıtla incelendi, yapısal hatalar düzeltildi)*

Prompt "bir tahmini kesin hata diye sunma" diyor. Aşağıdakiler **WPF'nin kendi
kaynak kodundan** okunmuş olgulardır; hangisinin bu makinede belirleyici olduğu
ise çalışma zamanı kanıtı gerektirir ve o kanıtı toplayacak mekanizma eklendi.

**Okunan kaynak.**
`dotnet/wpf` → `PresentationFramework/System/Windows/Appearance/WindowBackdropManager.cs`
ve `System/Windows/ThemeManager.cs`, `System/Windows/Window.cs`.

**Olgu 1 — WPF zaten Mica uyguluyordu.** `Window.ThemeMode` (uygulama hem
`App` hem `MainWindow` üzerinde `ThemeMode.System` kuruyordu) Fluent temasını
devreye alır. `Window.CreateSourceWindow` içinde:

```
if (ThemeManager.IsFluentThemeEnabled) ThemeManager.ApplyStyleOnWindow(this);
```

`ApplyStyleOnWindow` ise `WindowBackdropManager.SetBackdrop(window, MainWindow)`
çağırır; bu da sırasıyla `DwmExtendFrameIntoClientArea`,
`CompositionTarget.BackgroundColor = Transparent` ve
`DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW)` yapar.
Yani 22H2 ve sonrasında **ilk kareden önce** Mica isteniyordu.

**Olgu 2 — uygulamanın kendi yolunda çerçeve genişletme yoktu.**
`WindowBackdrop.TryApply` yalnızca `DwmSetWindowAttribute` çağırıyordu.
`DWMWA_SYSTEMBACKDROP_TYPE` malzemeyi seçer; malzemenin **istemci alanının
altına** çizilmesini sağlayan çağrı `DwmExtendFrameIntoClientArea`'dır (-1
kenar boşluklarıyla). WPF'nin yöneticisi bunu çağırıyor, uygulamanınki
çağırmıyordu.

**Olgu 3 — iki yönetici, tek pencere.** Hangisi sonra çalışırsa o kazanır. Bir
tema değişimi WPF'nin yöneticisini yeniden çalıştırır ve kullanıcının seçtiği
Acrylic sessizce Mica'ya döner.

**Olgu 4 — tek yönlü kapı.** `App.PersistBackdropOptOut`, pencere zaten
önündeyken uygulama ikinci kez başlatıldığında `DisableWindowBackdrop = true`
yazıyordu ve arayüzde bunu geri alacak hiçbir şey yoktu. Kısayolunu iki kez
tıklamış bir kullanıcı için Mica **kalıcı olarak** kapanmış olurdu.

**Değişiklik.**

- WPF'nin arka plan yöneticisi belgelenmiş anahtarıyla devre dışı:
  `Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop`
  (`DpiBypass.App.csproj` → `RuntimeHostConfigurationOption`; üretilen
  `DpiBypass.runtimeconfig.json` içinde doğrulandı). Fluent'in denetim
  görünümü ve paleti etkilenmez; yalnız arka plan yönetimi taşınır.
- `Infrastructure/WindowBackdrop.cs` yeniden yazıldı. Sıra: çerçeveyi genişlet
  → malzemeyi adlandır → **yalnız ikisi de kabul edildikten sonra** üstünü
  boyamayı bırak. Aradaki her reddediş opak yüzeyi geri koyar; genişletme
  reddedilirse malzeme de geri alınır, çünkü aksi hâlde istemci alanı
  hiçbir şey çizmeyen bir birleştiriciye devredilmiş olurdu — görünmez pencere
  hatasının tam şekli budur.
- `Remove` artık birleştirme hedefini siyaha değil pencerenin kendi rengine
  ayarlıyor (siyah, yeniden boyutlandırma sırasında görülen titremeydi).
- **Görünüm seçimi**: Sistem / Mica / Acrylic cam / Düz.
  `Core/Config/AppearanceMode.cs` içindeki `BackdropPolicy`, isteği makinenin
  durumuyla birleştirip tek bir malzemeye çevirir ve **her zaman bir cümle
  gerekçe** döndürür.
- Ayarlardan bir malzeme seçmek, izleyicinin oturum boyu kapatmasını **temizler**.

**Kanıt.** `BackdropPolicyTests` (21 test):

- "Sistem" = Mica; asla sessizce Acrylic olmaz (Microsoft'un ana pencere için
  önerisi Mica'dır, makinenin çizebildiği en camsı şey değil).
- 22621 öncesi her yapı düz yüzeye düşer ve yapı numarasını gerekçede söyler.
- Birleştirme kapalı / yazılımsal işleme / uzak masaüstü / yüksek karşıtlık →
  düz yüzey, her biri kendi gerekçesiyle.
- **Saydamlık efektleri kapalıyken veya pil tasarrufu açıkken Acrylic düz
  yüzeye değil Mica'ya düşer** — Mica duvar kâğıdı tonudur, canlı bulanıklık
  değildir, ve ikisinde de çizilmeye devam eder. Düz yüzeye düşmek ayarın
  istediğinden fazlasından vazgeçmek olurdu.
- Yalnızca **açıkça seçilmiş** bir malzemenin verilememesi "düşürüldü" sayılır;
  "Sistem" üzerinde kalan kullanıcı tam olarak o ayarın vaat ettiğini almıştır.
- Her sonucun boş olmayan bir gerekçesi vardır.
- Eski `DisableWindowBackdrop` bayrağı yeni ayara taşınır ve iki yönde de
  tutarlı kalır.

**Çalışma zamanı kanıtı toplama.** "Mica görünmüyor" üzerine işlem yapılamaz.
Artık ayarlardaki **Teknik ayrıntılar** başlığı altında, kararın kurulduğu
girdilerin hepsi yazılıdır: Windows yapı numarası, masaüstü birleştirme,
donanım hızlandırma (WPF işleme katmanı), saydamlık efektleri anahtarı, yüksek
karşıtlık, uzak oturum, pil tasarrufu ve DWM'nin döndürdüğü HRESULT. Aynı satır
günlüğe de yazılır.

**Çalışma zamanı kanıtı (Windows CI, yapı 26100).** Windows koşucusunda
yayımlanan uygulama artık pencereyi kuruyor ve ilk kareyi çiziyor; tanılama
satırı da tam olarak tasarlandığı gibi çalışıyor:

```
Pencere görünümü · istenen=Sistem · uygulanan=Mica · neden=Sistem: Mica uygulandı.
 · yapı=26100 · birleştirme=açık · hızlandırma=açık · saydamlık=açık
 · yüksek karşıtlık=kapalı · uzak oturum=hayır · pil tasarrufu=kapalı
```

Yani bu ortamda DWM malzeme isteğini **kabul etti**, çerçeve genişletildi ve
istemci alanı birleştiriciye devredildi. Bu, "Mica uygulandı" iddiasının
doğrulanmış hâlidir. Bir sonraki bölümdeki uyarı hâlâ geçerli: DWM'nin bunu
gerçekten **çizdiği** ekrana bakılmadan bilinemez.

**Doğrulanamayan.** Bu düzeltmelerin **hangisinin** kullanıcının makinesinde
belirleyici olduğu. Dördü de gerçek yapısal hatadır ve dördü de düzeltildi;
hangisinin görünürlüğü geri getirdiği ancak Windows 11 22H2+ bir makinede
uygulamayı çalıştırıp yukarıdaki tanılama satırını okuyarak söylenebilir.
Acrylic ve Mica'nın gerçekte nasıl göründüğü de ölçülmedi.

---

## 3. Bağlantı denetimi ve animasyonlar *(uygulandı, durum makinesi test edildi)*

**Değişiklik.** `Core/Connection/ConnectionFlow.cs`: Hazır → Ağ kontrol
ediliyor → Profiller deneniyor → Bağlantı doğrulanıyor → Bağlı, artı İptal
ediliyor, Bağlantı kesiliyor ve Bağlanamadı. Durum makinesi **XAML'de değil
Core'da**, çünkü buradaki üç hatanın üçü de sıralama hatasıdır:

- İkinci tıklama tek bir deneme başlatmalı.
- Kullanıcı iptal ettikten sonra dönen bir ölçüm eski bir aşamayı geri
  koymamalı — her aşama raporu ait olduğu **nesil numarasını** taşır.
- Aynı deneme içinde sırasız gelen bir aşama ekranı geriye yürütmemeli.

Hiçbir yerde yüzde yoktur: aday sayısı bilinir ama her birinin süresi bilinmez,
ve tahmin edilemeyen bir hızla dolan bir çubuk, ölçüm gibi sunulan bir
tahmindir.

Arayüzde: 196 DIP'lik bir halka içinde 132 DIP'lik yuvarlak düğme. Halka
çalışırken döner, başarıda yerini tam bir yeşil çembere ve onay simgesine
bırakır, hatada kırmızıya döner. Basma tepkisi 0.96 ölçek (80 ms), aşama
metinleri 130-200 ms, durum dönüşümü 300 ms — hepsi `Tokens.xaml`'den.
Animasyonların tamamı `opacity` ve `RenderTransform` üzerinden; hiçbir yerde
blur ya da yerleşim değiştiren animasyon yok. Dönme, `MotionEnabled`
(Windows'un animasyon tercihi **VE** uygulamanın kendi anahtarı) ile kapanır;
kapalıyken halka durur ama görünür kalır, yani durum yine de okunur.

**Kanıt.** `ConnectionFlowTests` (17 test): sıralı ilerleme; ikinci basışın hiç
şey yapmaması; iptal edilmiş denemeden gelen aşamanın ve hatta **başarının**
reddedilmesi; geriye yürümenin reddedilmesi; her aşamadan Bağlı ve Bağlanamadı
kabulü; hata sonrası yeniden denemenin yeni bir nesil açması; motorun kendi
başına gelen durumunun uçuştaki denemeyi geçersiz kılması; aynı aşamanın
gereksiz yere ekranı titretmemesi ama sayaç değişiminin geçmesi; 64 iç içe
basma/iptal/geç rapor sonunda tek tutarlı durum.

**Doğrulanamayan.** Animasyonların gerçekte nasıl göründüğü ve hissettirdiği;
süre değerlerinin bu uygulama için doğru olup olmadığı. Değerler
`Tokens.xaml`'de tek yerdedir, tam da çalışan uygulamada ayarlanabilsin diye.

---

## 4. Karşılama *(uygulandı, karar ve yerleşim test edildi)*

**Değişiklik.** Pencerenin içerik alanını kaplayan bir katman. `WindowState`,
`WindowStyle`, boyut ve konum **hiç** değişmez; kaplanan yalnızca istemci
alanıdır, sistem başlık çubuğu ve üç düğmesi yerinde kalır. Uygulama kabuğu
boyanmakla kalmaz **collapse** edilir, böylece Tab ile arkasındaki bir denetime
ulaşılamaz; karşılama bittiğinde odak gezinme çubuğuna taşınır.

`Core/Onboarding/WelcomeFlow.cs` kimin karşılanacağına karar verir ve karar
"başlatma" değil **"varış"** üzerinedir: kısayola çift tıklamak bir varıştır;
oturum açma görevinin uygulamayı tepsiye başlatması değildir; sabahtan beri
çalışan bir oturumda pencerenin tepsiden geri gelmesi de değildir.

Metin promptta verilen dört cümledir. İlk açılışta dört kartlık tanıtım (Geri /
İleri / Atla), sonrakilerde 1.6 saniye sonra kendiliğinden kapanan tek satırlık
karşılama. "Açılışta göster" saklanır; tanıtım ayarlardan yeniden açılabilir.

**Kanıt.** `WelcomeFlowTests` (14 test): ilk elle açılış tanıtımı alır ve
"açılışta göster" kapalı olsa bile alır (hiç görmemiş biri onu kapatmış
sayılamaz); sonraki elle açılış kısa karşılamayı alır; tercih kapalıysa hiçbiri
gösterilmez; Windows'un başlattığı açılış, tepsiye başlayan açılış ve tepsiden
dönüş kimseyi karşılamaz; dört kart kısa ve tek cümle; **metin uygulamanın
yapmadığı hiçbir şeyi vaat etmiyor** (VPN, anonimlik, şifreleme — test bunu
zorunlu kılıyor); markup pencereyi büyütmüyor/boyutlandırmıyor ve kabuk
gerçekten collapse ediliyor.

**Doğrulanamayan.** Karşılamanın gerçek ekrandaki görünümü ve geçiş
animasyonunun akıcılığı.

---

## 5. Ölçüme dayalı profil seçimi *(uygulandı, politika ve tarama test edildi)*

**Bulgu.** Eski tarama tek bir sayıyı tek bir hedefte karşılaştırıyordu: her
adayı kurup `discord.com`'a TLS bağlantısı açıyor, **ilk geçen denemede
duruyor**, üç başarı toplandıktan sonra tarama bitiyor ve en düşük `Elapsed`
kazanıyordu. Dört ayrı sorun:

1. **Tek hedef ağ değildir.** `discord.com`'a ulaşıp `gateway.discord.gg`'ye
   ulaşamayan bir tarif bağlanır ve sonra bir sesli görüşmeyi taşıyamaz; eski
   tarama buna kazanan diyordu.
2. **Tek örnek ölçüm değildir.** İlk geçişte durmak, kazananın temiz bir paket
   yakalayan aday olması demekti; sabit 30 ms ile 10/50/10/50 ayırt
   edilemiyordu.
3. **Tek sayı karşılaştırma değildir.** `Elapsed` = DNS + bağlanma + el
   sıkışma; yani aşma tariflerinin karşılaştırması kısmen kriptografinin
   karşılaştırmasıydı.
4. **En düşüğü seçmek politika değildir.** Hız kaybı, kararsızlık ve sürekli
   profil değiştirme hesaba katılmıyordu.

**Değişiklik.**

*Ölçüm modeli* (`StrategyMeasurement.cs`): hedef/yöntem, DNS çözümleme, TCP
bağlanma, TLS el sıkışma **ayrı alanlarda**; başarısızlık oranı, medyan,
dalgalanma (üç örneğin altında `null`), yayılım. Hız ve yük altındaki gecikme
artışı ayrı bir kayıtta ve ölçülmediyse `null`. **Paket kaybı alanı yoktur** —
reddedilen bir TLS el sıkışması, telde düşen bir paket değildir — ve bir test
bunun hiç eklenmediğini doğruluyor.

*Tarama* (`StrategyTuner.cs`), üç aşama:

1. **Eleme.** Aday başına tek deneme, tek gerekli hedefe. On iki tarifli
   kütüphaneyi dört adaylık kısa listeye indiren ucuz soru.
2. **Ölçüm, dönüşümlü.** Her turda her aday bir kez kurulur ve **her hedef**
   denenir. A'yı beş kez sonra B'yi beş kez ölçmek, taramanın ikinci yarısında
   ağın yaptığı her şeyi (Wi-Fi geçişi, başkasının indirmesi, DNS önbelleğinin
   ısınması) B'nin hanesine yazar. Dönüşümlü ölçüm bunu hepsine dağıtır;
   kimsenin denetlemediği bir hat üzerinde karşılaştırmayı anlamlı kılan tek
   şey budur. Varsayılan 3 tur × 3 hedef = aday başına 9 örnek: medyan ve
   dalgalanma için yeter, p95 için yetmez — dolayısıyla hiçbir yerde p95
   iddia edilmez.
3. **Hız (yalnız istendiğinde).** Aşağıya bakınız.

*Seçim politikası* (`StrategySelectionPolicy.cs`) sırası: **erişim →
kararlılık → hız → gecikme**. Gerekli hedeflerin tamamına ulaşamayan aday
elenir; denemelerinin %20'sinden fazlası başarısız olan elenir; hız ölçüldüyse
en iyisinin %95'inin altında kalan elenir; kalanlar arasında en düşük bağlanma
süresi, eşitlikte en düşük dalgalanma kazanır. Sonra **histerezis**: bir
rakibin mevcut profili değiştirebilmesi için hem 3 ms hem %8 daha iyi olması
gerekir, ve çalışan bir profil 10 dakika korunur. Sonuçları birbirinin
gürültüsü içinde kalan iki profil arasında sürekli geçiş, her seferinde bir
yeniden bağlanma demektir.

Eşiklerin **hepsi** `SelectionThresholds` içinde adlandırılmış alanlardır,
karşılaştırmanın içine gömülmüş sabitler değil — %95 dâhil. Bir test bunu
doğruluyor: eşik 0.7'ye çekildiğinde kazanan değişiyor.

*Veri harcayan hız testi.* Varsayılan **kapalı**. Tarama her ağ değişiminde
çalıştığı için, açık bir varsayılan kullanıcının mobil kotasını onun adına
harcardı. "Hızı da ölçerek tara" düğmesi bir kez çalıştırır ve **basılmadan
önce** ne kadar veri kullanacağını söyler (aday başına 6 MB × 4 aday ≈ 24 MB);
yanındaki anahtar kalıcı tercihtir. `CloudflareThroughputProbe` bilinen sayıda
bayt indirir — sunucunun boyutuna karar verdiği bir aktarım bütçelenemez — ve
hat meşgulken gidiş-dönüşü örnekleyerek **kuyruk gecikmesini** ayrıca raporlar.
Bu ayrım RFC 6349'un yönteminden alınmıştır ve **RFC 6349 uygulaması
değildir**; kodda bunun neden olmadığı (temel RTT/darboğaz türetmesi yok, TCP
pencere boyutlandırması yok, aktarım süresi oranı yok, tek yön) yazılıdır.

*Bağlantı yokken tarama yok.* El sıkışmanın ortada kesilmesi, yutulan bir
ClientHello ya da başkasının sertifikası bir **süzgecin çalıştığını** gösterir.
DNS hatası ya da hiç tamamlanmayan bir bağlantı ise kablonun takılı olmadığını
gösterir ve hiçbir tarif bunu düzeltmez. Üst üste üç aday ikinci türden
başarısız olursa tarama durur ve bunu söyler.

*Arayüz.* Etkin profilin yanında **gerekçe**, **ölçüm zamanı** ve **doğrulama
düzeyi** yazılıdır. Doğrulama düzeyi hız testinin çalışıp çalışmadığını açıkça
söyler ("hız testi yapılmadı"). Elenen adaylar ve eleme nedenleri açılır bir
başlık altında listelenir.

**Kanıt.**

- `StrategySelectionTests` (21 test): en düşük bağlanma süresi kazanır; gerçek
  hız kaybettiren aday, en hızlı bağlanan olsa bile elenir; hız eşiğinin
  içindeki aday gecikme avantajını korur; hızlı ama kararsız aday elenir;
  gerekli hedefi kaçıran aday eleniyor ve **hangi hedefi** kaçırdığı gerekçede
  yazıyor; yetersiz örnek ölçüm sayılmaz; hiçbir şey ölçülmediğinde mevcut
  profil korunur; anlamsız iyileşme profili değiştirmez ama anlamlı iyileşme
  değiştirir; yeni kurulmuş profil korunur ama **çalışmayı bırakmış profil
  beklemeden** değiştirilir; hız ölçülmediğinde sonuç bunu söyler; hızı
  ölçülmemiş aday hız kuralıyla elenmez (bilinmeyen, kötü değildir); her
  sonucun gerekçesi vardır; eşik gerçekten ayarlanabilir; eşit medyanlar
  dalgalanmayla ayrılır; el sıkışma süresi bağlanma süresi değildir;
  başarısızlıklar başarısızlık olarak sayılır ve paket kaybı olarak asla.
- `StrategySweepBehaviourTests` (24 test): açık ağ tek denemede bildirilir;
  eleme aday başına tek denemedir ve kısa liste dolunca durur; **ölçüm aşaması
  dönüşümlüdür** (bloklar kesin olarak dönüşümlü); her hedef ölçülür; tek
  hedefe ulaşan aday kazanmaz; hiçbir şey geçmeyen tarama geri yükler; ölçüm
  bağlanma ve el sıkışmayı ayrı tutar; hız testi yoksa bunu söyler; devredışı
  bırakılmış tarama hiçbir şey kurmaz (`finally` geri yazması dâhil); yarıda
  devredışı kalan tarama hemen durur; iptal edilen tarama oturumun kullandığını
  geri koyar; hatırlanan profil doğrulaması **her gerekli hedefi** denetler;
  süre bütçesi dolduğunda tarama biter ve bunu bildirir; başarısız deneme asla
  bir süre değeri katmaz; hız ölçümü yalnız istendiğinde ve aday başına bir kez
  çalışır, maliyeti önceden bilinir; **tamamlanmayan aktarım adayı yavaş değil
  ölçülmemiş bırakır**; hız aktarımı gecikme turları bitmeden asla başlamaz;
  bağlantısız makinede tarama erken durur ama gerçekten süzgeçlenen ağ bununla
  karıştırılmaz.

**Doğrulanamayan (önemli).** Gerçek bir ağ üzerinde hiçbir ölçüm yapılmadı.
%95 hız eşiği, 3 ms / %8 iyileşme eşiği, 10 dakikalık bekleme, 4 adaylık kısa
liste ve 3 tur — hepsi **gerekçelendirilmiş başlangıç değerleridir**, ölçülmüş
optimumlar değil. Prompt zaten bunu istiyor ("Bu eşiği değişmez bilimsel gerçek
sayma; testlerle ayarla"); bu kod onları ayarlanabilir kıldı ve her birinin
yanına ne yaptığını gösteren bir test koydu, ama ayarlamanın kendisi gerçek
ağlarda yapılmalıdır. Cloudflare ölçüm ucunun `?bytes=N` ile istenen kadar bayt
döndürdüğü bu ortamdan `curl` ile doğrulandı (2 000 000 bayt istendi, 2 000 000
bayt geldi); uygulamanın içinden hiç çağrılmadı.

---

## 6. Performans

**Yapılan.** Yeni animasyonların tamamı `opacity` ve `RenderTransform` — WPF'nin
yeniden yerleşim yapmadan birleştirdiği iki özellik. Hiçbir yerde
`DropShadowEffect` yok: bulanıklık, taşıyan her yüzeyde kare başına bir GPU
geçişidir ve bu pencerenin 2016 model bir ultrabook'ta akıcı kalması
gerekiyor; derinlik yüzey rengi ve saç teli kenarlıkla taşınıyor. Bu karar
`Tokens.xaml` içinde görünür biçimde yazılı. Halkanın dönmesi pencere gizliyken
zaten durur (mevcut `SetPresentationActive` yolu) ve hareket tercihi kapalıyken
hiç başlamaz.

Ana ekrandaki **güncel trafik** değeri bağdaştırıcının kendi sayaçlarından
okunur; trafik üretmez ve yalnızca pencere ekrandayken (var olan 2 saniyelik
sunum zamanlayıcısı) okunur — tepsideki bir uygulama kimsenin göremeyeceği bir
etiket için ağ arayüzlerini saymaz.

**Doğrulanamayan.** Açılış süresi, boştaki CPU/RAM, bağlantı süresi ve ağ
sonuçlarının önce/sonra karşılaştırması. Bunların hiçbiri ölçülmedi çünkü
uygulama çalıştırılmadı. Depoda bu ölçümler için zaten `docs/startup-performance.md`
ve `docs/background-footprint.md` var; bu geçiş onlara sayı eklemedi.

---

## 7. Doğrulama: ne yapıldı, ne yapılamadı

**Yapılan.**

- `dotnet build` (Release) — 0 uyarı, 0 hata.
- `dotnet test` — 1268 test, hepsi başarılı (referansta 1155).
- `dotnet publish` — self-contained win-x64 paket üretildi; `DpiBypass.exe` ve
  `DpiBypass.Recovery.exe` mevcut. WinDivert ikilileri **yok**, çünkü onları
  derleme hattı çekiyor (`tools/fetch-windivert.ps1`) ve bu ortamda
  çalıştırılmadı.
- `runtimeconfig.json` içinde WPF arka plan anahtarının gerçekten bulunduğu
  doğrulandı.
- XAML kaynak denetimi (eksik ve ileri `StaticResource` başvuruları) hem yeni
  birim testiyle hem de PowerShell betiğinin Python'da yeniden uygulanmasıyla
  çalıştırıldı: 289 anahtar, 596 başvuru, sorun yok.
- `scripts/tests/xaml-resources.tests.ps1` düzeltildi: "nokta içeren anahtar
  çerçeveye aittir" kuralı, `Space.8` ve `Motion.Fast` gibi her tasarım
  jetonunu sessizce atlar hâle gelmişti; artık yalnız `SystemColors.` /
  `SystemParameters.` / `SystemFonts.` ön ekleri atlanıyor ve
  `{StaticResource {x:Type …}}` biçimindeki örtük stil başvuruları
  tanınıyor.

**Yapılamadı.**

- **Uygulama bu ortamda hiç çalıştırılmadı.** Windows yok, ekran yok, WinDivert
  sürücüsü yok, ekran görüntüsü alınmadı. Windows CI koşucusu uygulamayı
  çalıştırdı: pencere kuruluyor, ilk kare çiziliyor ve Mica uygulanıyor (yukarı
  bakınız) — ama koşucu da bir ekrana bakmıyor, yalnız yerleşimi ve çizimi
  doğruluyor.
- Açık/koyu tema, Mica/Acrylic/düz, karşılama, bağlantı/iptal/hata ve yüksek
  DPI durumları **gözle** denetlenmedi.
- %100/%125/%150/%200 ölçeklerde taşma/kırpılma denetlenmedi.
- Klavye odağı ve ekran okuyucu davranışı çalışan uygulamada denenmedi
  (erişilebilir adlar ve `LiveSetting` markup'ta var, odak devri kodda var).
- **Lenovo LOQ 15AHP10 ve HP EliteBook 840 G3 üzerinde hiçbir test
  yapılmadı.** Bu cihazlarda test yapıldığı iddia edilmiyor.
- PowerShell betikleri (`scripts/tests/*.ps1`) bu ortamda çalıştırılamadı.

**Bunun için yapılan.** `Infrastructure/UiLayoutSelfTest.cs` genişletildi.
`DpiBypass.exe --ui-selftest`, Windows'ta gerçek bir WPF penceresi kurar ve CI
bunu her derlemede çalıştırıp ürettiği PNG'leri artefakt olarak yükler. Artık:

- Pencere boyutu kümesi **760×560, 820×620, 1080×780** (önce ikisi vardı).
  760×560 pencerenin kendi asgari boyutudur ve bu geçişte küçültüldü: eski
  asgari 620 DIP yükseklik, 1366×768 bir dizüstünde %125 metin ölçeğinde kalan
  614 DIP'e **sığmıyordu** — yani HP EliteBook 840 G3'ün tipik yapılandırması.
- Her boyut × her palet için **durum sayfası** (bağlantı denetimi), **ayarlar
  sayfası** (görünüm kartı) ve **karşılama** ayrı ayrı çiziliyor ve
  kaydediliyor: 24 PNG.
- Karşılama denetleniyor: kabuk gerçekten collapse oluyor, pencere durumu,
  boyutu ve konumu değişmiyor (önce/sonra ölçülüyor), katman içerik alanının
  tamamını kaplıyor, her kart pencereyi taşırmıyor ve son karttan sonra
  uygulama geri geliyor.
- Bağlantı denetimi denetleniyor: düğme durum sayfasında, kahraman boyutunda ve
  yerleşmiş durumda.

Yani Windows'ta bir derleme çalıştıran herkes — ve her CI koşusu — bu
değişikliklerin gerçek ekran görüntülerini üretir. Bu geçişte üretilmedi.

---

## 8. Değişen dosyalar

**Yeni**

| Dosya | Ne |
| --- | --- |
| `src/DpiBypass.App/Theme/Tokens.xaml` | Tasarım jetonları: boşluk, yarıçap, tipografi, kenarlık, simge boyutu, hareket |
| `src/DpiBypass.App/Theme/Icons.xaml` | Üretilen dosya: 132 Fluent geometri (46 simge × 2 boyut × varyant) |
| `src/DpiBypass.App/Infrastructure/FluentIcon.cs` | Simge + boyut → doğru çizim |
| `src/DpiBypass.App/ViewModels/MainViewModel.Appearance.cs` | Görünüm seçimi ve hareket tercihi |
| `src/DpiBypass.App/ViewModels/MainViewModel.Connection.cs` | Bağlantı akışının arayüz tarafı |
| `src/DpiBypass.App/ViewModels/MainViewModel.Overview.cs` | Ana ekranın dört kararı ve seçim gerekçesi |
| `src/DpiBypass.App/ViewModels/MainViewModel.Welcome.cs` | Karşılama |
| `src/DpiBypass.Core/Config/AppearanceMode.cs` | `AppearanceMode`, `BackdropEnvironment`, `BackdropPolicy` |
| `src/DpiBypass.Core/Connection/ConnectionFlow.cs` | Bağlantı durum makinesi |
| `src/DpiBypass.Core/Onboarding/WelcomeFlow.cs` | Kimin karşılanacağı ve tanıtım metni |
| `src/DpiBypass.Core/Diagnostics/StrategyMeasurement.cs` | Ölçüm modeli |
| `src/DpiBypass.Core/Diagnostics/StrategySelectionPolicy.cs` | Seçim politikası ve eşikleri |
| `src/DpiBypass.Core/Diagnostics/ThroughputProbe.cs` | Kullanıcının başlattığı hız ölçümü |
| `tools/fetch-fluent-icons.py` | Simge üreteci (`--check` ile doğrulama) |
| `tests/…/DesignSystemTests.cs` · `BackdropPolicyTests.cs` · `ConnectionFlowTests.cs` · `WelcomeFlowTests.cs` · `StrategySelectionTests.cs` · `StrategySweepBehaviourTests.cs` | 113 yeni test |

**Yeniden yazılan**

| Dosya | Ne |
| --- | --- |
| `src/DpiBypass.App/Infrastructure/WindowBackdrop.cs` | Malzeme yönetimi, kanıt kaydı, doğru sıra |
| `src/DpiBypass.Core/Diagnostics/StrategyTuner.cs` | Üç aşamalı, dönüşümlü, bütçeli tarama |

**Önemli değişiklikler**: `MainWindow.xaml` (bağlantı denetimi, karar
kutucukları, görünüm kartı, karşılama katmanı, hız düğmeleri),
`Theme/Shared.xaml` (jeton kullanımı, `FluentIcon` şablonu, bağlantı ve
karşılama stilleri), `ProtectionService.cs` (seçim kaydı, hız ölçümlü tarama),
`AppSettings.cs` (görünüm, hareket, karşılama, hız tercihi),
`UiLayoutSelfTest.cs`, `README.md`, `THIRD-PARTY-NOTICES.md`,
`.github/workflows/build-and-release.yml` (self-test zaman aşımı 60→90 sn).

---

## 9. Sırada ne var

Bunlar **yapılmadı** ve bilinçli olarak açık bırakıldı:

1. **Gerçek ağlarda eşik ayarı.** %95 hız tabanı, 3 ms / %8 iyileşme eşiği ve
   10 dakikalık bekleme ölçülmeli. `SelectionThresholds` tam da bunun için tek
   bir kayıt hâlinde duruyor.
2. **Oyun hedefiyle gecikme ölçümü.** Prompt "oyun gecikmesini mümkünse gerçek
   oyun hedefi/protokolüyle ölç" diyor. Depoda bunun altyapısı var
   (`MinecraftStatusProbe`, `ValorantHudLatencySource`), ama profil seçimi
   hâlâ TCP bağlanma süresini kullanıyor ve arayüz bunun ne olduğunu açıkça
   yazıyor. Profil seçimini oyun hedefine bağlamak ayrı bir iştir.
3. **Yükleme (upload) hızı.** `ThroughputResult.UplinkMbps` alanı var ve hep
   `null`. Kuyruk gecikmesinin çoğu yükleme yönündedir; bunu ölçmek promptun
   RFC 6349 bölümünün asıl hedefidir.
4. **Windows'ta gözle doğrulama ve ekran görüntüleri.** Yukarıda anlatılan
   `--ui-selftest` yolu bunu bir komutluk iş hâline getirdi.
