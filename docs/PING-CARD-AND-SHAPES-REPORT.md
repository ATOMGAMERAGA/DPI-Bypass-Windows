# Ping kartı, kapsül rozetler ve karşılama hareketi

Başlangıç noktası: `6c0b284` (PR #27'nin `main`'e birleştiği commit).
Bu belge yapılan işin kaydıdır, işin yerine geçmez. Her başlıkta **ne değişti**,
**hangi kanıtla** ve **neyin doğrulanamadığı** ayrı yazılıdır.

Bu geçiş de **Linux üzerinde** yapıldı. .NET 10 SDK ile hem derleme hem de birim
testlerinin tamamı çalıştırıldı (`EnableWindowsTargeting`), ancak uygulama bu
makinede **hiç çalıştırılmadı**: Windows, WinDivert sürücüsü ve ekran yok. Gerçek
çizim doğrulaması CI'daki `windows-latest` koşucusunda `--ui-selftest` ile
yapıldı; aşağıda bunun neyi yakaladığı ve neyi hâlâ kapsamadığı yazılı.

---

## 0. Kapılar

| Kapı | `6c0b284` | Bu dalda |
| --- | --- | --- |
| `dotnet build` (Core + App + Tests) | başarılı | başarılı, 0 uyarı |
| `dotnet test` | 1272 başarılı / 0 başarısız | **1319 başarılı / 0 başarısız** |
| `--ui-selftest` (gerçek WPF penceresi, CI) | başarılı | başarılı — **ama önce dört gerçek hatayı yakaladı**, bkz. §6 |
| `scripts/tests/*.ps1` (CI) | başarılı | başarılı — bir ileri başvuruyu yakaladıktan sonra, bkz. §6.5 |

Eklenen test sayısı: **48**.

---

## 1. Bozuk rozet şekilleri *(kök neden bulundu, ortak bileşende düzeltildi)*

**Bulgu.** WPF'in köşe yuvarlaması CSS'inki değil ve fark kozmetik değil. CSS'te
`border-radius: 999px` kısa ve geniş bir kutuyu kapsüle çevirir: tarayıcı her
yarıçapı `min(width, height) / 2` değerine kırpar. Aynı 999'u
`Border.CornerRadius`'a yazmak **elips** verir.

Uygulamanın rozetleri (tek tip köşe, tek tip kalınlık, düz renk fırça)
`Border.ArrangeOverride` tarafından basit çizim yoluna gönderiliyor; o yol da her
yuvarlatılmış dikdörtgeni `RectangleGeometry.GetPointList` üzerinden çözüyor:

```csharp
radiusX = Math.Min(rect.Width  * (1.0 / 2.0), Math.Abs(radiusX));
radiusY = Math.Min(rect.Height * (1.0 / 2.0), Math.Abs(radiusY));
```

İki eksen **ayrı ayrı** kırpılıyor ve hiçbiri `min(width, height) / 2` değerine
indirilmiyor. 46×19'luk bir rozette 999 bu yüzden (22.5, 9) oluyor: düz kenar
hiç kalmıyor, uçlar küt değil sivri. Karmaşık yol (`GenerateGeometry`) aşırı
yarıçapı taştığı kenara paylaştırıp aynı şekle varıyor, yani hangi yolu izlediği
fark etmiyor.

`Tokens.xaml:70`'teki `Radius.Pill = 999` güçlü bir aday değil, **kanıtlanmış kök
nedendi**. `Shared.xaml:104`'teki yorum önceki bir geliştiricinin *sonucu* fark
ettiğini ("stray oval") ama nedenini bulmadığını gösteriyor: o tek stil
`Radius.Tile`'a çevrilmiş, `Radius.Pill` diğer üç stilde bırakılmış.

**Değişiklik.** Kapsül artık bir sayı değil, bir davranış:
`Infrastructure/PillShape.cs`, `infra:PillShape.IsPill="True"` ile uygulanıyor ve
`CornerRadius`'u **`(ölçülen yükseklik − kenarlık) / 2`** değerinde tutuyor.
Yüksekliğin yarısı (9.5) yakın ama doğru değil: kontur, rozetten bir saç teli
kısa bir dikdörtgende gidiyor, 9.5 dikeyde 9'a kırpılıp yatayda 9.5 kalıyor ve
iki halka da hafif yumurta biçimli çıkıyor. Ölçülen yükseklik şart, çünkü bu
rozetler metne ve kullanıcının metin ölçeğine göre boyutlanıyor: %100'de doğru
olan sabit bir sayı %150'de yine elips.

`Radius.Pill` kaldırıldı; yerine neden kaldırıldığını anlatan bir not var.
Etkilenen stiller: `RecommendedTagBorderStyle`, `BetaBadgeStyle`,
`WelcomeDotStyle` (8×8 nokta doğruydu — kare, çevrelenmiş elipsin daire olduğu
tek durum; 22×8 etkin nokta elipsti). `Pad.Badge` yan boşluğu 10 DIP'e çıkarıyor
ki metin kendi uç kapağına binmesin.

**Kanıt.** `tests/DpiBypass.Tests/Ui/BorderGeometry.cs` WPF'in çözdüğü
yuvarlatılmış dikdörtgenlerin aynısını çözüyor (kaynak: dotnet/wpf `Border.cs` ve
`RectangleGeometry.cs`). `BadgeShapeTests` eski değeri elips, yenisini altı rozet
boyutunda kapsül olarak sabitliyor. Bu **algoritmanın modeli**, ekran görüntüsü
değil: WPF'ten hangi şeklin istendiğini kanıtlar, rasterleştiricinin ne çizdiğini
değil.

Gerçek çizim tarafında `UiLayoutSelfTest.VerifyBadgeShapes` üç pencere boyutu ve
iki palette yerleşmiş her kapsülü ölçüyor, ayrıca dört metin ölçeğinde bir rozet
galerisi PNG'si üretiyor (`artifacts/ui-selftest/badges-*.png`, CI artefaktı).

**Görsel doğrulama.** CI'ın ürettiği `badges-Light.png` indirildi ve **bakıldı**:
dört metin ölçeğinin dördünde de rozetler uçları yuvarlak kapsül, karşılama
noktaları daire ve etkin nokta kapsül olarak çiziliyor. Yani bu maddede iddia
yalnız geometriye değil, gerçek bir Windows çizimine dayanıyor.

---

## 2. Ping özelliği: tek Aç/Kapat *(uygulandı)*

**Bulgu.** Ping optimizasyonunu açmak tek bir eylem değil üç karardı: ölçüm modu
seç, test et, sonra uygula. Bu kararların hiçbiri kullanıcının niyetiyle ilgili
değil — uygulamanın kendi iç yapısıyla ilgili.

**Değişiklik.** Anahtar artık tek denetim. Hedefi
`Network/Latency/AutomaticLatencyTarget.cs` seçiyor ve kuralı bilerek katı:
canlı oturum, karşı tarafın dinlediği bir portta, süregelen ve açık bir akış
olacak; ikinciyi belirgin farkla geçecek; ve bu çıtayı **tam olarak bir**
uygulama karşılayacak. Karşılayan varsa o ölçülür. Yoksa ya da birden fazlaysa
genel internet referansı ölçülür ve nedeni yazılır — belirsiz bir tahmin oyun
pingi diye sunulmaz. `Automatic` yeni ayarların varsayılanı; açık türler ölçüm
mantığı olarak altta ve Ayrıntılar'da geçersiz kılma olarak duruyor.

Ana ekrandaki seçim kutuları kaldırıldı; `UiMarkupTests` artık kartın genişleyen
bölüm dışında **tek** bir denetimi olduğunu doğruluyor (anahtar; çalışan bir
ölçümü durduran İptal düğmesi hariç).

**Kapatma.** `SetLowLatencyModeAsync` önce tercihi yazıp sonra gecikme kapısını
bekliyordu; o kapıyı çalışan ölçüm tutuyor ve eşli kıyaslama dakikalar sürüyor.
Yani anahtarı kapatmak dakikalarca hiçbir şey yapmıyordu. Artık devre dışı
bırakma **önce çalışan denemeyi iptal ediyor**. Terk edilen ölçüm kendi iptal
yolundan geri alıyor, kapatma yolunun geri alması da bunu garantiye alıyor ve
eski sonucu mevcut nesil koruması düşürüyor.

**Kanıt.** `LatencyToggleTests` — dördü de düzeltme geri alındığında başarısız
oluyor (ikisi zaman aşımına uğruyor, yani "kapat" gelmeyen bir kapıda bloke
oluyor). Ayrıca sabitlenen iki şey: anahtar ile uygulanmış iyileştirme **ayrı
durumlar**, ve iki yön de bypass bağlantısına dokunmuyor.

---

## 3. "%5 ms artışı" — nereden geliyordu *(kök neden bulundu ve düzeltildi)*

**Önce bir dürüstlük notu.** Bildirilen mesajın **birebir aynısı bu deponun
geçmişinde hiç var olmadı**. `git log --all -S` ve tüm ref'ler üzerinde
`git grep` ile arandı, sonuç yok. Yani bu, ekranda okunanın bir aktarımı; kodda
öyle bir dize aranmadı, o okumayı **üreten mekanizma** arandı ve bulundu.

**Mekanizma.** `LatencyOptimizer`'ın `NoGain` yolları, geri almadan **önceki**
ölçümü sonucun `After` alanına veriyordu:

- her aday elendiğinde `reference` — son adayın hâlâ uygulanmışken ölçülen değeri;
- paket doğrulaması geçilemediğinde `final` — kabul edilen paketin uygulanmışken
  ölçülen değeri.

`LatencyStatusView.From` ise `After`'ı hem `IdleAfter`'a hem güncel `Idle`'a
taşıyordu. Sonuç: reddedilen adayın *daha kötü* ölçümü, "değişiklik geri alındı"
başlığının hemen altında **kullanıcının şu anki pingi** olarak yazılıyordu. Aynı
alan çalışma sırasında da yayımlanıyordu, yani henüz karar verilmemiş adayların
değerleri de sızıyordu.

**Neden tam olarak "%5" ve neden "devre dışı".** Medyan kötüleşme eşiği
`max(1 ms, taban × %5)` ve karşılaştırma **kesin büyüktür**. 48 → 50.4 ms için
`2.4 > 2.4` yanlış: %5'lik bir artış eşiği kıl payı ıskalıyor. Bu yüzden çalışma
"kötüleşme" demiyor, "kazanç yok" diyor — kart da bu yüzden zarar adlandırmak
yerine devre dışı bırakıldığını söylüyor — ve yanındaki sayı reddedilen adayın
ölçümü oluyordu. `LatencyRollbackReportingTests.FivePercentWorseIsExactlyOnThe
BoundaryAndReadsAsNoGain` bu sınırı sabitliyor.

**Değişiklik.** Ölçümler artık ne için alındıklarını taşıyor
(`LatencyMeasurementRole`: `Baseline`, `Candidate`, `Verification`,
`PostRollback`, `Reference`) ve yanında çalıştıkları oturum, ağ, bağdaştırıcı ve
o an yürürlükteki ayarlar var. Ayarları artık uygulanmayan bir ölçüm **güncel
durum olarak okunamıyor**.

Geri alan bir çalışma **yeniden ölçüyor**: bağdaştırıcının oturmasını bekliyor,
hedefin erişilebilirliğini denetliyor ve taze bir ölçüm alıyor. Alamazsa
`Remeasure = Failed` diyor ve **hiçbir sayı göstermiyor** — önceki değeri
kopyalamıyor. Hiçbir şey tutulmadıysa `After` null, yani çizilecek bir
önce/sonra çifti de yok.

**Kararlılık ile düşüş ayrıldı.** `LatencyComparison.ConfirmsMeaningfulImprovement`
p95, p99 veya jitter iyileşmesini de kabul ediyordu ve bu "ping şu kadar düştü"
diye sunuluyordu. Artık `ConfirmGain` `Median` mi `Stability` mi olduğunu
söylüyor; eşikler ve kötüleşme denetimi aynı, yani kapı gevşetilmedi.

---

## 4. Önce / Sonra / Kazanç *(uygulandı)*

`Network/Latency/LatencyHeadline.cs` kartın tamamına çekirdekte karar veriyor,
yani kurallar pencere olmadan test edilebiliyor ve bir binding ile atlatılamıyor:

- Önce ve sonra **aynı karşılaştırmanın aynı istatistiği** (medyan RTT) ve yalnız
  onları üreten değişiklik hâlâ uygulanmışken. Hiçbir şey tutmayan bir çalışmanın
  çifti yok.
- Yalnız medyan kazancı bir düşüş, yalnız düşüş yeşil. Kuyruk/jitter kazancı
  kendi cümlesini alıyor: *"Bağlantı daha kararlı…"* — milisaniyeye çevrilmiyor.
- Farklı hedef ya da farklı ağ üzerindeki karşılaştırma çıkarılmıyor, **reddediliyor**.
- Ölçülmeyen değer `—`; sahte `0 ms` yok. Yeniden ölçüm başarısızsa söylüyor.
- Yüzde ikincil ve yalnız taşıyabilen bir taban varsa; `0` veya negatif tabanda
  `null`.
- Şu anki ping, üstündeki çiftten **ayrı etiketli**: o çift bir karşılaştırmanın
  geçmişi, bu ise bağlantının şu an okuduğu değer.
- İyileşme yoksa: *"Belirgin bir düşüş doğrulanmadı; mevcut ayarlarınız korundu."*
- Geri alma başarısızsa durum "Mevcut ayarlar korundu"ya yumuşatılmıyor.

Teknik olan her şey — metrik kutucukları, şeritler, hedef seçiciler, elle yeniden
çalıştırma, p95 — kapalı **Ayrıntılar** bölümünde.

`LatencyHeadlineTests`: 13 test, her biri yukarıdaki maddelerden birini sabitliyor.

---

## 5. Karşılama *(uygulandı)*

Işık (radyal gradyan, `BlurEffect` değil — bulanıklık altındaki her şey üzerinde
kare başına bir GPU geçişi olurdu), marka simgesinin hafif aşımla yerleşmesi ve
simge → başlık → cümle sırasıyla 70 ms arayla gelmesi. Adımlar arasında aynı
hareket, çünkü kart hâlâ indekse bağlı.

Kısıtlar niyet değil test: hiçbir storyboard tekrarlamıyor, hiçbir şey
bulanıklaşmıyor ve tüm giriş ilk kareden **380 ms** sonra bitiyor — testlerin
uyguladığı 400 ms bütçesinin içinde. İlk hâli 440 ms'ti; bütçe yükseltilmedi,
hareket kısaltıldı. Eylem çubuğunun kendi girişi 130 ms ve gecikmesiz; self-test
Atla ve açılış anahtarının **ilk karede** görünür, etkin, yerleşmiş ve tıklanabilir
olduğunu, ışığın da tıklamayı engellemediğini doğruluyor.

Hareket kapalıyken tamamlanmış bir tasarım var (aynı boyut, aynı boşluk, aynı
hiyerarşi) ve kart, ışık, marka birlikte değiştiriliyor.

Değişmeyen: hangi karşılamanın ne zaman çıktığı. İlk tanıtım, sonraki kısa
karşılama, "Açılışta göster", Ayarlar'dan yeniden açma ve tepsiden dönüş hâlâ
`WelcomeFlow.Decide`'dan geçiyor; self-test karşılamanın pencereyi büyütmeden,
taşımadan ve biçimini değiştirmeden içerik alanını kapladığını doğrulamayı
sürdürüyor.

---

## 6. CI'nın yakaladığı gerçek hatalar

Bu, "Windows'ta gerçek render ile doğrula" isteğinin neden haklı olduğunun
kanıtı. Ping kartının kazanç rengini `Theme/Shared.xaml`'deki bir stil
belirliyor; adını verdiği dönüştürücü ise `MainWindow.xaml`'in
`Window.Resources`'ında tanımlıydı. `StaticResource` yalnız yazıldığı sözlüğü ve
o sözlüğün kendinden önce birleştirdiklerini görür — bir pencerenin kendi
kaynakları bu zincirde değildir.

Sonuç: **derleme temiz geçti, 1307 birim testi geçti**, ve uygulama Windows'ta
ayarlar sayfasının şablonunu ilk kurduğunda

```
Cannot find resource named 'GainBrushConverter'
```

diye patladı. `PingPercentStyle` aynı kusuru `EmptyToCollapsedConverter` ile
taşıyordu.

Dönüştürücüler `Shared.xaml`'e taşındı. `EveryStaticResourceReferenceNamesAKey
ThatExists` bunu yakalayamazdı çünkü bütün dosyaların anahtarlarını tek kümede
topluyordu, yani **hiçbir kapsam modellemiyordu**; artık her dosyanın
başvurularını o dosyanın gerçekten görebildiklerine karşı çözüyor.

### 6.2 Karşılama kartı boş çiziliyordu

Pencere yeşile döndükten sonra üretilen PNG'lere **bakıldı** ve karşılama
kartının yerinde çıplak bir `0` olduğu görüldü. Kartı, `ApplyMotionPreference`
içinden bir takma ad anahtarını iki şablondan birine yönelterek seçmek gerçek
çizimde çalışmadı; `ContentTemplate` hiçbir şeye çözülünce sunucu bağlı değeri
(kart indeksi) olduğu gibi çiziyor. Sessiz bir başarısızlık: derleme, birim
testleri ve pencere sağlık denetimi hepsi geçiyordu.

Seçim artık `MotionEnabled` üzerinde bir tetikleyici — bağlantı halkasının zaten
kullandığı birleşik tercih — ve `ContentTemplate` sunucuya değil **stile**
yazılıyor, çünkü yerel bir değer tetikleyiciyi yener. Işık, marka ve eylem
çubuğu da aynı koşula taşındı; bu arada `Loaded` tetikleyicilerinin bir hatası
da düzeldi: karşılama kaldırılmıyor, **daraltılıyor**, yani `Loaded` uygulamanın
ömrü boyunca bir kez çalışırdı.

Self-test artık karta güvenmiyor, **bakıyor**: şablonun kurulmuş olması ve view
model'in gösterdiği başlık, gövde ve simgenin kartın içinde bulunması şart.

### 6.3 Kazanç rakamı koyu temada görünmüyordu

Yine karelere bakılarak bulundu: koyu temada "Kazanç" kutucuğundaki `—`
neredeyse görünmezdi. Nedeni, rengi bir dönüştürücünün seçmesiydi. Bir
dönüştürücü **bağlaması değişince** çalışır; tema değişmesi `ShowsReduction`'ın
değişmesi değildir. Dolayısıyla ilk çalıştığında yüklü olan paletin fırçası
donuyor ve koyu temada açık temanın neredeyse siyah metin rengiyle, neredeyse
siyah bir kutucuğun üzerine çiziliyordu.

Renk artık `DynamicResource` taşıyan bir tetikleyiciden geliyor — dosyanın geri
kalanının zaten kullandığı biçim — ve `GainToBrushConverter` silindi. Self-test
de artık markup'a değil, elemanın gerçekten tuttuğu fırçaya bakıyor: her ping
rakamının rengi **o an yüklü paletin** bir fırçası olmak zorunda.

**Not (bu geçişte düzeltilmedi).** Aynı biçim `SeverityBrushConverter` ve
`CheckStateBrushConverter`'da da var; `MainWindow.xaml`'de beş yerde
kullanılıyorlar ve aynı gizli kusuru taşıyorlar: çalışma sırasında tema
değişirse bağlı değer değişmediği sürece renkleri eskisinde kalır. Bunlar bu
çalışmadan önce de vardı ve her biri çok değerli bir girdi alıyor, yani
tetikleyiciye çevirmek bu işin kapsamı dışında bir değişiklik olurdu. Bulgu
olarak burada duruyor.

### 6.4 Anahtarın etiketi sonucu bildiriyordu

Aynı karelerde görüldü: anahtar `PingCard.Status`'a bağlıydı, yani son
çalışmanın **sonucunu** gösteriyordu — oysa anahtar kullanıcının **tercihini**
taşır. Ayrı tutulması istenen tam olarak bu iki durumdu ve bir anahtarın üstünde
"Mevcut ayarlar korundu" yazabilirdi. Artık kendi işaretli durumundan Açık/Kapalı
okuyor; başlığın altındaki durum sözcüğü ölçümlerin bulduğunu söylemeye devam
ediyor.

---

## 7. Doğrulanamayanlar

Bunlar eksik değil, **yapılamayan** şeyler; sonuç uydurulmadı.

1. **Gerçek ping kazancı ölçülmedi ve iddia edilmiyor.** Bu makinede Windows,
   ağ bağdaştırıcısı, WinDivert sürücüsü ve gerçek bir ağ yok. Bu geçiş
   ölçümlerin **nasıl raporlandığını** düzeltir; hiçbir ağda düşüş vaat etmez.
   Raporlanan tek sayısal sonuç testlerin kendi sentetik ölçümleridir.
2. **Yerel darboğaz azaltma tarafında yeni müdahale eklenmedi.** Mevcut kod
   incelendi ve istenen korumaların zaten var olduğu doğrulandı: adaylar
   kablosuz/Ethernet, güç kaynağı, taşıma katmanı ve işlemci sayısına göre
   filtreleniyor (`LatencyCandidateContext`); profil önbelleği elenen adayları
   tekrar denemiyor; süre ve veri bütçeleri var (`CandidateBudget`,
   `TotalBudget`); Traffic Guard'ın `MinimumRetainedThroughputShare` tabanı hızı
   ciddi düşüren bir kazancı kabul etmiyor. Bunlar bu geçişte **yazılmadı**,
   yalnız doğrulandı.
3. **Ekran görüntülerine bakıldı, ama hepsine değil.** CI'ın ürettiği
   `artifacts/ui-selftest/*.png` indirildi; rozet galerisi (açık palet) ve ping
   kartı ile karşılama (koyu palet, 1080) incelendi. Rozetlerin dört metin
   ölçeğinde de düzgün kapsül çizildiği ve karşılama kartının §6.2'den önce boş
   olduğu **görülerek** doğrulandı. Diğer 20 kare açılmadı.
4. **Karşılamanın hareketli hâli CI'da çizilmiyor.** Koşucu
   `SystemParameters.ClientAreaAnimation = false` bildiriyor ("Hareket azaltma
   etkin" günlüğü), yani CI'ın çizdiği **durağan** varyant. Hareketli yol için
   kanıt markup testleri ve bütçe denetimidir; gerçek karesi alınmadı. Bu aynı
   zamanda §6.2'nin neden yalnız durağan yolda görüldüğü anlamına gelir — hareketli
   yol da aynı takma ad mekanizmasını kullanıyordu, yani aynı hatayı taşıyordu.
5. **%100/%125/%150/%200 DPI.** Self-test üç pencere boyutu ve iki paleti
   geziyor; DPI ölçeğini değiştirmiyor. Rozet geometrisi bu ölçeklerde
   `BadgeShapeTests` ile sayısal olarak, galeri PNG'sinde dört metin ölçeğiyle
   görsel olarak kapsanıyor — ama gerçek bir yüksek DPI ekranında değil.
6. **PowerShell betik testleri** bu makinede çalıştırılamadı (PowerShell yok);
   CI'da "Run the script and resource tests" adımı olarak geçiyor. Bu adım bir
   **ileri başvuruyu** yakaladı (`PingSwitchStyle`, kendisinden 400 satır sonra
   tanımlanan `SwitchStyle`'a `BasedOn` ile bağlıydı) ve aynı kural artık
   `DesignSystemTests` içinde de var, yani Linux'ta da kırılıyor.
