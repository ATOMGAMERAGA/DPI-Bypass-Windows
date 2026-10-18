# Sosyal Erişim Deneyleri — Alpha

Bu özellik başlangıçta kapalıdır. Eski ayarlar MANUAL moduna göç eder; AUTO_BETA kendiliğinden başlamaz. Bozuk Alpha ayarı normal DPI ayarlarını sıfırlamaz. Kod, gerçek mevcut motoru kullanan geri alınabilir denemeler ve sınırlı içerik gözlemi sunar. Yeni bir yaş/kimlik doğrulama bypass'ının sahada çalıştığı iddia edilmez.

## Durum ve kapsam

| Eksen | 18 Ekim 2026 durumu |
|---|---|
| IMPLEMENTATION_STATUS | IMPLEMENTED: iki mod, gerçek adapter'lar, companion, recovery ve kullanıcı GUI / ayrıcalıklı motor ayrımı entegre. Yetenek kapsamı aşağıdaki gibidir. |
| AUTOMATED_TEST_STATUS | 1.424 .NET testi, 20 companion testi, WPF build/publish, Debug/Release gerçek GUI self-test ve dört mevcut PowerShell test script'i PASS. |
| DEVICE_TEST_STATUS | Yerel Windows GUI ve gerçek güvenli IPC PASS. Aynı companion gerçek Chromium 153'te izole Linux fixture ortamında uçtan uca PASS. Windows fiziksel driver/UAC/ağ veri yolu saha doğrulaması NOT_RUN. |
| FIELD_EFFECT_STATUS | FIELD_UNVERIFIED. Gerçek hesap/hedef/ISP erişim deneyi yapılmadı. |

Ana uygulama artık `asInvoker` manifest'iyle masaüstü kullanıcısı olarak açılır. Mevcut `ProtectionService`, yalnız `--engine-helper` başlatılan penceresiz yönetici yardımcısında bulunur; GUI `IProtectionController`/`RemoteProtectionClient` üzerinden aynı normal koruma/latency/DNS/hotspot/domain API'sini kullanır. Driver gerektiğinde yalnız helper UAC ister. Browser, Alpha coordinator, journal ve gözlem GUI tarafındadır. Eski yükseltilmiş logon task'ından gelen GUI açılışı kullanıcı token'ına devredilir; yeni logon task'ı LeastPrivilege'dır. Native messaging host da yükseltilmez.

Helper kanalı rastgele oturum pipe'ı, yalnız aynı kullanıcı ACL'si, medium integrity, gerçek PID/executable/token denetimi ve yalnız sabit controller sözleşmesiyle sınırlıdır. Sayfalar/browser host bu mutasyon kanalına erişmez. 256 KiB request / 2 MiB response sınırı ve kısa frame-read deadline vardır. Normal uzun latency testleri için 35 dakika üst sınır korunurken Alpha'nın 20 saniyelik sınırı ayrıdır. Cancel, Stop ve snapshot uzun ölçümün arkasında beklemez; kayıttan önce gelen cancel da korunur. GUI ölünce helper mevcut servisini dispose eder; tek engine-owner dosya kilidi eski/yeni helper'ın çakışmasını engeller. DNS recovery watchdog gerçek motor sahibini izler.

GUI ayar nesnesi sabit kalır; geç snapshot kullanıcı düzenlemesini ezmez, queued UI save helper'ın öğrendiği network profillerini silmez. İlk pencere önbellekteki salt okunur durumla çizilir; ayar kaydı UAC bekleyerek UI'yi kilitlemez. Kurtarma journal'ındaki bilinmeyen stage veya geçersiz bütçe yeni apply/cleanup yetkisi oluşturmaz. Debug ve üretilen Release paketinin `--ui-selftest` kipleri ayrı ayrı exit 0 verdi; bu kip driver/DNS açmaz ve kurulu örneğin kilidine/denetim kanalına katılmaz.

| Yetenek | Otomatik | Kullanıcı adımı / sınır |
|---|---|---|
| A0 ağ tanısı | Evet; bounded gerçek HTTPS probu | HTTP/TLS sonucu içerik ya da hesap başarısı değildir. |
| A0 içerik baseline | Destekli companion hedefinde | Companion kurulumu/bağlama ve veri tercihi gerekir. Native baseline kullanıcı bildirimi veya UNKNOWN'dur. |
| Resmî web yüzeyi | Evet, açıkça bağlı companion ile | Manuelde varsayılan resmî browser yolu açılır. Web sonucu native onarımı değildir. |
| Mevcut DPI stratejisi | Evet, gerçek StrategyCoordinator lease'i | Normal koruma açık, hedef mevcut kapsamda ve kapsam tercihi kabul edilmiş olmalıdır. |
| İçerik gözlemi | YouTube desktop `/watch`, observer/evaluator 1.0.0 | Bilinen DOM, doğru video, görünür belge ve taze yol kanıtı gerekir. Canlı DOM uyumluluğu henüz denenmedi. |
| Instagram / native içerik | Ağ tanısı var; otomatik içerik yok | Manuel inceleme. Genel hesap/uygulama gözlemi veya ekran kazıma yok. |
| Aynı hesap | Yalnız açık session binding ve uygun kanıtla | Popup beyanı kimlik doğrulaması değildir; hesap ID/cookie okunmaz. Oturum değişimi gözlemi geçersizleştirir. |
| Yavaşlık onarımı | Hayır | Performans NOT_MEASURED; içerik açılması hız iyileşmesi sayılmaz. |

## Kullanım

1. Ağ ayarlarından **Sosyal Erişim…** penceresini açıp Alpha'yı etkinleştirin. Platform, sorun, kimlik bağlamı, kabul edilen yüzey ve gereken yetenekleri seçin. Native/aynı hesap hedefi sessizce anonim web hedefiyle değiştirilmez.
2. Belirli resmî HTTPS içerik bağlantısını girin. URL yalnız aktif bellekte kalır. YouTube watch/shorts ve Instagram p/reel şekilleri doğrulanır; keyfî host/port/yerel ağ URL'si kabul edilmez.
3. Mevcut motor kapsamını ve gerekiyorsa renderer'ın tahmini veri kullanımını ayrı kabul edin. Motor stratejisi, mevcut koruma kapsamındaki yeni bağlantıları etkiler; tek sekme izolasyonu sunmaz. DNS/QUIC değişmez.
4. **Manuel Test** uygun adayı uygular. **Hâlâ var** geri aldıktan sonra sonraki adaya geçer; **Yok, açıldı** aramayı durdurur ve gerekli kaynağı korur; **Test edemedim** geri alıp bekletir. Başka uygulamaya geçmek tek başına başarısızlık değildir. 15 dakika yanıtsızlık kaynak temizliğiyle beklemeye alır.
5. **Otomatik Test — Beta** aynı adayları ve coordinator'ı kullanır. Kanıtlı FAIL geri alıp ilerler; PASS durur; belirsiz/desteksiz sonuç manuel incelemeye ayrılır. HTTP 200, hazır video, reklam veya yanlış hedef PASS oluşturmaz. Autoplay engeli kullanıcı eylemi ister; aynı deneme yeni apply olmadan sürdürülebilir.
6. **Bende hâlâ sorun var** otomatik kararı düzeltir, hedef/goal/observer sürümü için yerel yanlış pozitif engeli koyar ve manuel devam sağlar. Kullanıcının **Yok, açıldı** bildirimi belirsiz teknik sonucu ezip yeni aramayı durdurur. Mod değişiminden gelen eski sonuç reddedilir.
7. **Durdur / geri al**, kapatma, ağ/normal ayar değişimi ve Alpha'yı devre dışı bırakma yalnız Alpha sahipliğini temizler. Beklenen mevcut değer değişmişse yeni kullanıcı ayarı ezilmez; RECOVERY_REQUIRED yeni denemeyi engeller. Normal koruma kapanmaz. Rapor önce yerel olarak gösterilir, yalnız kullanıcı kaydeder; otomatik yükleme yoktur.

## Companion kurulumu ve gözlem sınırı

Companion kendiliğinden kurulmaz veya browser kaydı değiştirmez. Kaynak `browser-companion/` altında, sabit extension ID `figpmfdodbgjjdhlfadcdkigimbbelll`, host adı `net.atom.dpibypass.social` olur. Yalnız Chromium manifest/native messaging yolu paketlenir; doğrulanmış Chrome/Edge sürümü veya Firefox desteği iddia edilmez.

Yerel build sonrası, kendi masaüstü kullanıcınızla:

```powershell
# Repo kökünde; mevcut .NET 10 SDK ile:
dotnet publish src/DpiBypass.SocialHost/DpiBypass.SocialHost.csproj -c Release -o artifacts/social-host
pwsh -File browser-companion/install-windows.ps1 -HostExecutable "$PWD/artifacts/social-host/DpiBypass.SocialHost.exe"
```

Bu ikinci komut HKCU Chrome/Edge native host kaydı ve `%LOCALAPPDATA%/DPI Bypass/browser-companion/` altında kullanıcıya ait manifest yazar; Program Files'e yazma/admin izni gerekmez. Bu çalışma sırasında Windows kullanıcı browser kaydı **değiştirilmedi**. Browser'ın eklenti yönetiminde `browser-companion/` klasörünü unpacked olarak açıkça yükleyin. Uygulamada Alpha'yı başlatın, popup'tan aynı hedefi gözlem penceresinde bağlayın ve test sekmesini görünür tutun. Önceden bağlama NO_ACTIVE_ATTEMPT verdiyse aktif gözlem sırasında tekrar bağlayın. Kaldırmak için aynı script'i yalnız `-Uninstall` ile çalıştırın.

Native host yalnız `social.bind`/`social.observe` taşır: sabit origin, aynı kullanıcı pipe ACL'si, 16 KiB frame, 3 saniye IPC deadline. Sayfa sistem komutu/apply API'sine erişmez. Content script ham HTML, özel mesaj, cookie, token, ekran görüntüsü veya hesap ID'si göndermez. Seçili hedefe bağlı yerel DOM/medya sinyalleri değerlendirilir.

Companion'ın sahip olduğu test sekmesi 20 saniyelik lease ve tek seferlik alarm ile sınırlıdır; borrowed kullanıcı sekmesi taşınmaz, kapatılmaz veya duraklatılmaz. Başarıda ya da manuel devre geçişte sahip olunan medya duraklatılıp görünen sekme kullanıcıya devredilir. Daha sonraki Stop bu normal kullanıcı sekmesini kapatmaz. Browser suspend/timer gecikmeleri kesin gerçek zamanlı byte kesmesi sağlamaz. Ayrıntılar companion README'sindedir.

Otomatik PASS: doğru hedef, bilinen gate yokluğu, izinli yüzey ve kimlik, uygun yetenekler, görünür/fresh belge, en az iki tutarlı örnek/en az iki saniye. Medya hedefinde en az üç saniye doğal oynatma, seek/reklam/preview olmaması gerekir. Service worker/cache belirsizliği, tanınmayan DOM veya sürüm/şema farkı INCONCLUSIVE üretir. Native uygulamanın veya yeniden kullanılan browser TLS soketinin N4 stratejisinden geçtiği kanıtlanamaz: N4 browser route provenance **UNVERIFIED** kalır, manuel inceleme gerekir. Baseline bilinmiyorsa dar web erişim gözlemi nedensel onarım iddiasına dönüşmez.

## Aday kanıt tablosu

Yerel temel `8ac250f`; kaynak incelemesi 17–18 Ekim 2026. IMPLEMENTED gerçek handler çağrısını ifade eder; canlı bypass başarısını ifade etmez. Uygunluk normal motor/capability/izin/hedef koşullarıyla her denemede denetlenir. Aynı effectKey/no-op yeniden denenmez.

| Aile | Durum | Mekanizma, yol, yan etki ve sınır |
|---|---|---|
| A0 | IMPLEMENTED | Müdahalesiz bounded HTTPS ve kullanıcı/companion baseline. Zaten açık hedefte apply 0; yöntem başarı kredisi yok. |
| A1 | IMPLEMENTED | Aynı içerik için resmî HTTPS web yolu; native→web açıkça izinli olmalı. Manuel browser sekmesi kullanıcıya aittir. Özgün yüzey zaten web ise no-op elenir. |
| A2 | MANUAL_GUIDED | Resmî browser sunumu kullanıcı tarafından seçilebilir; doğrulanmış ayrı Windows presentation handler yok. UA taklidi yapılmaz. |
| A3 | MANUAL_GUIDED | Kullanıcı browser guest/profile açabilir. Alpha profil/cookie temizlemez, hesap taşımayı veya geçici profil cleanup'ını sahiplenmez. |
| A4 | RESEARCH_ONLY | Güncel, bağımsız resmî YouTube/Instagram eski arayüzü doğrulanmadı. Reddit anekdotu aktarılmadı. |
| A5 | IMPLEMENTED | A1'in açıkça izinli anonim/public kapsamı; aynı etki ikinci aday diye sayılmaz. Hesap/private/DM başarısı değildir. |
| A6 | RESEARCH_ONLY | yt-dlp/NewPipe için kurulu, sürümü sabitlenmiş ve tüm fallback yolları doğrudan çıkışlı adapter doğrulanmadı; istemci indirme/kurulum yok. |
| N1 | MANUAL_GUIDED | Mevcut normal DNS/DoH kontrolleri var; Alpha hedefe özel yeni DNS handler'ı değildir. Normal ayar değişimi aktif denemeyi geçersizleştirir. |
| N2 | RESEARCH_ONLY | Hedefe bağlı dual-stack/NAT64 lease ve geri alma kanıtı yok; sistem IPv6'sı kapatılmaz. |
| N3 | MANUAL_GUIDED | Mevcut normal QUIC ayarı kullanılabilir; bütün mevcut kapsamı etkileyen anahtar otomatik Alpha adayı diye çoğaltılmaz. |
| N4 | IMPLEMENTED | Mevcut StrategyLibrary profilleri; Passthrough/current no-op hariç. StrategyCoordinator exclusive lease, expected-current restore ve gerçek bounded prob. Mevcut motor kapsamı; yeni packet engine, DNS veya firewall kuralı yok. Sunucu hesap/gate şartında N4 döngüsü elenir. |
| N5 | RESEARCH_ONLY | Gerçek ECH negotiation attestation yok; GREASE/rastgele extension ECH başarısı sayılmaz. |
| N6 | RESEARCH_ONLY | Seçili medya→CDN bağlantısını güvenilir strateji yoluna bağlayan adapter yok; rastgele IP/CDN override yapılmaz. |

EXCLUDED: uzak VPN/proxy/relay/Tor/WARP, proxy bağımlı eski bypass script'leri, uzak Invidious/Redlib, ülke/SIM/GPS değiştirme, başka hesap/token, sahte kimlik/yaş, TLS doğrulaması kapatma ve gate'i yalnız CSS ile gizleme. Tarihsel Simple-YouTube-Age-Restriction-Bypass güncel ve doğrudan çıkışlı bağımsız yöntem kabul edilmedi.

Ek araştırma hipotezleri: (1) browser bağlantısını gerçek motor lease'ine bağlayan socket provenance, yeni bağlantı/OS/browser sürümü ölçümü gerektirir; yanlış/reused socket ilk çürütme ölçütüdür, maliyeti platform entegrasyonudur. (2) Sürümü sabit yerel extractor, resmî hedefe doğrudan ulaşırsa ayrı yüzey sunabilir; herhangi bir uzak relay/cookie gereksinimi adayı eler, bakım maliyeti yüksektir. (3) Gerçek ECH destekli client, DNS/server negotiation kanıtıyla değerlendirilebilir; GREASE veya handshake başarısının içerik başarısına çevrilmesi hipotezi çürütür. Bunların hiçbiri uygulanmış yöntem olarak sayılmaz.

## Sahiplik, bütçe ve gizlilik

Tek coordinator session/attempt/operation/context/mode/document nesillerini ve event/state sürümünü denetler. Strateji niyeti atomik, flush edilmiş journal'a apply'dan önce yazılır. Eski normal tuner lease'i yeni Alpha sahipliğini ezemez. Geri alma beklenen installed/before değerlerini karşılaştırır; timeout sonrası reconcile tamamlanmadan sonraki aday başlamaz. Strateji yalnız süreç belleğindedir; restart'ta eski başarı/apply replay edilmez. Bilinmeyen/bozuk recovery kaydı Alpha'yı bloklar.

Grup sınırları: 12 aday, 24 diagnostic istek, 5 MiB diagnostic aktarım, 120 saniye aktif çalışma; prob/gözlem başına 20 saniye, renderer en fazla 6 yükleme. Veri maliyeti ölçülemeyen/unknown durumda renderer için açık bir oturum tercihi gerekir; renderer byte sınırı ESTIMATED'dır. Bütçe normal kullanıcı trafiğini kesmez. Rate limit Retry-After ile cooldown üretir, hızlı başka-aday yağmuru oluşturmaz. Kalan adaylar ve belirsiz adayların manuel devamı ayrı korunur.

`%LOCALAPPDATA%/DPI Bypass/social-alpha/` içinde kurtarma journal'ı ve ayrı tanı/policy kaydı bulunur. Rapor/özet tam URL, HTML, cookie, hesap kimliği, ham IP veya SSID içermez. Özet saklama 14 gün/en fazla 100 grup; arka plan prune/probe yoktur. Yanlış pozitif için minimum hedef/goal/version digest'i ve cooldown yerelde tutulur. Geçmiş temizliği etkin recovery bilgisini silmez. Export yalnız kullanıcı önizleme/kaydet eylemiyle yapılır.

## Birincil kaynaklar ve kanıt sınırı

- [Chrome native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging), [content scripts](https://developer.chrome.com/docs/extensions/develop/concepts/content-scripts), [worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle): protokol/izin/yaşam döngüsü kaynağı; kurulu gerçek browser testi değildir.
- [Microsoft CreateProcessWithTokenW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw), [DuplicateTokenEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-duplicatetokenex), [GetShellWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getshellwindow): kullanıcı-token handoff tasarımı; gerçek UAC/desktop senaryosu NOT_RUN.
- [Yerel Windows motoru](https://github.com/ATOMGAMERAGA/DPI-Bypass-Windows), [ByeDPI](https://github.com/hufrea/byedpi): mevcut yerel packet/desync mekanizmaları hesap yetkisini değiştirmez.
- [yt-dlp Extractors](https://github.com/yt-dlp/yt-dlp/wiki/Extractors), [PO Token](https://github.com/yt-dlp/yt-dlp/wiki/PO-Token-Guide), [tarihsel script](https://github.com/zerodytrash/Simple-YouTube-Age-Restriction-Bypass): extractor/oturum/relay sınırları; PO Token yaş kanıtı değildir.
- [ECH RFC 9849](https://datatracker.ietf.org/doc/html/rfc9849): negotiation tanımı; mevcut motorun ECH desteği kanıtı değildir.

Tüm T01–T48 cihaz/saha kabul senaryolarının geçtiği iddia edilmez. Ortak fixture ve gerçek yerel adapter/protokol testlerinin kapsamı ile açık kapılar `social-access-alpha-delivery.md` dosyasındadır.
