# Sosyal Erişim Alpha — son teslim

**18 Ekim 2026.** Üç repoda MANUAL ve AUTO_BETA, gerçek mevcut-motor adapter'ları, platform gözlemcileri, geri alma/kurtarma, bütçe/cooldown ve ayar/UI entegrasyonu uygulandı. Feature flag kapalı, göç varsayılanı MANUAL'dır. Kullanıcının “Yok, açıldı” bildirimi aramayı durdurur; bilinmeyen/yanlış hedef kanıtı PASS oluşturmaz.

Başlangıç, devam ve GitHub teslimi öncesinde üç repoda `git pull --ff-only origin main` başarılıydı. Temeller Windows `8ac250f`, Android `3032d6c`, Linux `79e286e`; dallar `codex/social-access-alpha`. Kaynak kodu, testler ve belgeler bu dallardan GitHub'a teslim edilir. Resmi release veya kurulu kullanıcı uygulamasına güncelleme yapılmadı.

## Dört ayrı durum

| Eksen | Windows | Android | Linux |
|---|---|---|---|
| IMPLEMENTATION_STATUS | IMPLEMENTED; kullanıcı GUI / ayrıcalıklı helper ayrımı tamamlandı | IMPLEMENTED; gerçek VpnService/ByeDPI ve kontrollü WebView | IMPLEMENTED; mevcut daemon/proxy, GTK ve native companion |
| AUTOMATED_TEST_STATUS | **1.424/1.424 .NET**, **20 JS**, dört PowerShell script'i; WPF build/publish PASS | **53/53 JUnit**, **6 JS**, tam Kotlin/Compose/API/JNI/APK build PASS | Gerçek Linux **318 test**, exit 0 / 5 açık skip; gerçek GTK **4**, netns **2**, native IPC **2**, ayrıcalıksız erişim **53** PASS |
| DEVICE_TEST_STATUS | Debug/Release gerçek GUI self-test ve gerçek güvenli pipe/peer/cancel PASS. Fiziksel WinDivert/UAC/ağ geçişi saha testi NOT_RUN | API35 x86_64 WHPX emülatöründe APK/ayar/mod kalıcılığı ve gerçek VPN izin/başlangıç/revoke/cleanup smoke PASS. Fiziksel/OEM/metered koşullar NOT_RUN | Gerçek Linux/kernel, GTK/libadwaita, nft/iptables netns ve Chromium153 companion akışı PASS. Kullanıcının fiziksel ağ geçişleri NOT_RUN |
| FIELD_EFFECT_STATUS | FIELD_UNVERIFIED | FIELD_UNVERIFIED | FIELD_UNVERIFIED |

Geliştirme/test teslimi gerçek hesap/yaş/kimlik/ISP erişim garantisi değildir. Native aynı hesap gözlenemiyorsa teknik sonuç UNKNOWN kalır. Performans NOT_MEASURED; yavaşlık hedefi otomatik çözülmüş sayılmaz. Baseline UNKNOWN ise causalAttribution NOT_ESTABLISHED'dır. Kullanıcı hesabı veya sosyal işlem kullanılmadı.

## Kalan uygulama işleri nasıl kapatıldı

Windows artık `asInvoker` GUI'sinde `IProtectionController`/`RemoteProtectionClient` kullanır; mevcut `ProtectionService` yalnız penceresiz yönetici helper'ında oluşturulur. Browser, Alpha coordinator ve observer kullanıcı sürecindedir. Yeni logon task LeastPrivilege; eski yükseltilmiş açılış kullanıcı token'ına devredilir. Sabit controller sözleşmesi, gerçek PID/image/user/elevation denetimi, user-only ACL/medium label, bounded framing ve eşzamanlı cancel vardır. GUI ölümü helper teardown'unu tetikler; engine-owner OS kilidi iki helper'ın çakışmasını engeller. DNS watchdog gerçek motor sahibini izler. Normal DNS/latency/hotspot/domain/CLI API'si korunmuştur.

Gerçek IPC testlerinde bulunan audit privilege/listener çakışması ve cancel-before-registration yarışı düzeltildi. Sayaç/IP/endpoint/tuple snapshot'ları kayıpsız taşınır. GUI ayar nesnesi, kaydedilmemiş düzenleme ve helper'ın network profilleri korunur. İlk pencere çizilirken UAC bekleyen senkron save yoktur. Geçersiz journal stage/bütçesi kaynak işlemine izin vermez. Companion kayıt manifest'i kullanıcı LocalAppData'sındadır; Program Files yazma izni istemez.

Android'de izole JDK/Gradle/SDK/NDK/CMake toolchain sağlanıp tam build çalıştırıldı. Gerçek derlemede bulunan OkHttp `Dns.lookup` hatası düzeltildi. Windows Git'in symlink header placeholder'ları pinned submodule'a dokunmadan build staging içinde doğrulanan include forwarder'lara dönüştürüldü. İki JNI motoru arm64-v8a, armeabi-v7a ve x86_64 için derlendi; debug APK imzası doğrulandı.

Linux'ta bozuk recovery budget çökmesi, baseline 429 cooldown eksikliği ve observer hata nedeni düzeltildi. Kullanıcının açık aktarım onayıyla gerçek Linux suite/GTK/kernel/browser kontrolleri yürütüldü. Host firewall veya sistem paketi değiştirilmedi; kaynaklar salt okunur, browser offline/geçici profile sahip, firewall testleri ayrı network namespace'dedir.

Panel'e üç reponun kaynak anlık kopyaları aktarıldı ve üçünün arşiv SHA-256 değeri doğrulandı. Resmî SHA-256 denetiminden geçen Node24.10.0 ile Panel'de Windows companion **20**, Android observer **6**, Linux companion **20** test geçti; son Linux kaynak arşivinden tam suite tekrar **318 test / 5 skip**, exit 0 verdi. Windows WPF/.NET ve Android Gradle/JNI doğrulamaları kendi yerel Windows toolchain'lerinde yapıldı. Panel log kopyaları Windows `artifacts/panel-validation/` altındadır.

## Gerçek browser ve emülatör kanıtı

Chromium **153.0.8010.52**: sabit extension ID, gerçek native host→Unix socket→coordinator, owned sekme, gerçek service-worker stop/wake, session/attempt/renderer bütçesinin korunması ve Stop cleanup doğrulandı. Yerel karşılanan resmî hedef DOM/canvas-medya fixture'ı gerçek content/native transport üzerinden PASS; gate fixture'ı FAIL ve cleanup üretti. TLS gevşetilmedi; internet/hesap kullanılmadı. Log: Linux `build/linux-validation/real-browser.log`. Bu fixture etkisidir; canlı YouTube DOM/ISP başarısı değildir.

Android **API35 x86_64**, emulator **37.2.12**, WHPX: ilk Alpha kapalı/MANUAL görüldü; AUTO_BETA seçimi process restart'ında korundu ve MANUAL'e dönüldü. Alpha kapatıldı. Wi-Fi/mobil veri kapalıyken gerçek Android VPN izni kabul edilip foreground DpiVpnService/JNI/TUN başlangıcı görüldü. Android Settings→VPN→Forget VPN ile izin iptal edildi; arayüz “Koruma kapalı”ya döndü, aktif servis temizlendi, uygulama crash buffer'ı boş kaldı. Kanıtlar Android `.local-tools/emulator-evidence/` altındadır. Fiziksel cihaz/saha veya native hesap başarısı sayılmaz.

Göreve ait emülatör ve yalnız test için kullanılan 5038 portundaki ADB sunucusu kapatıldı. Kullanıcının kurulu Windows uygulaması değiştirilmedi.

## Komut ve çıktı kaydı

| Komut / kontrol | Sonuç ve kanıt |
|---|---|
| Windows `dotnet test ...DpiBypass.Tests.csproj --no-restore --verbosity quiet` | teslim öncesi tekrar exit 0; **1.424 PASS**, 0 fail/skip; SDK10.0.402. `artifacts/final-dotnet-tests.log` |
| Windows WPF build, `--no-restore --disable-build-servers -m:1` | exit 0; **0 warning/error**, SDK10.0.401. `artifacts/broker-app-build.log` |
| Windows Release publish, `-o artifacts/social-alpha-app -p:PublishReadyToRun=false` | exit 0; runtime/driver/Recovery/companion paketlendi. `artifacts/broker-publish.log` |
| Debug ve Release `DpiBypass.exe --ui-selftest` | ikisi exit 0; gerçek pencere/yerleşim, kurulu örneğe katılmaz, driver/DNS açmaz |
| install / latency-harness / setup-waits / xaml-resources PowerShell testleri | dört script exit 0; installer veya benchmark çalıştırılmaz |
| Native host binary negatif kontrolleri | yanlış origin exit 2; oversized frame/yasak komut exit 3; üç kontrol PASS |
| Windows/Linux `node --test browser-companion/*.test.cjs` | her repoda **20 PASS**, Node24.19.0 |
| Android `gradlew.bat :app:testDebugUnitTest :app:assembleDebug --no-daemon` | exit 0; **53 PASS**, 0 fail/error/skip; Gradle8.9, JDK17, SDK35, NDK26.3.11579264, CMake3.22.1 |
| Android `apksigner verify --verbose app-debug.apk` | exit 0; geliştirme v2 imzası geçerli |
| Android `node --test tests/observer.test.cjs` | **6 PASS**; ayrıca saf Kotlin18 ve grammar51 dosya kontrolleri geçti, tam build yerine sayılmadı |
| Linux `python3 -m unittest discover -s tests -q` | exit 0; **318 test/5 skip**, Python3.14.7, Linux6.8. `build/linux-validation/full-suite-final.log` |
| Gerçek GTK4/libadwaita/Xvfb | **4 PASS**, GTK4.22.5/libadwaita1.9.4; stub yok |
| Gerçek nft/iptables `unshare --net` | **2 PASS**; apply/conflict/rollback sırasında normal kurallar korundu |
| Ayrıcalıksız native framing/Unix IPC ve manifest | **2 PASS**, UID1000, gerçek subprocess/socket |
| Ayrıcalıksız Linux erişim suite | **53 PASS**, skip yok |
| Gerçek Chromium companion runner | exit 0; **6 uçtan uca kontrol PASS** |
| Üç repo `git diff --check --ignore-submodules=all` | exit 0; yalnız CRLF/LF notices |

Linux'un beş skip'i geçilmiş sayılmaz: root olmayan erişim-helper testi ayrıca UID1000 koşusunda geçti; rawfake minimal capability ortamında, üç eski latency netns sınıfı opt-in olmadığı için skip kaldı. Ayrı NET_RAW koşusunda TCP_REPAIR eksikliği sürdü. Sosyal özelliğin nft/iptables testleri ayrıca geçti. Ortam/log ayrıntıları Linux rehberindedir.

Üç dil aynı **32 evaluator örneği** ve **8 olay dizisini** tüketir. Ortak olaylar T01/T02/T03/T04/T07/T17/T23/T29 için phase/finishReason/kaynak sayısı/sonraki adayı karşılaştırır. Diğer regresyonlar recovery/budget/replay/mode/context/identity/media/cache/observer/correction/lease/IPC/permission yollarını kapsar. T20 uzak relay ve T22 geçici browser profili adapter'ı eklenmediği için N/A'dır. Bütün T01–T48'in her fiziksel cihazda/sahada geçtiği iddia edilmez.

## Adaylar, rehber ve çıktılar

Windows A0/A1/A5/N4; Android A0/A1/A5/N4; Linux A0/A1/A2(YouTube)/A5/N4 gerçek adapter kapsamındadır. A5 aynı etkiyi ikinci kez saymaz. Diğer aileler platforma göre MANUAL_GUIDED/EXCLUDED/RESEARCH_ONLY olarak rehberlerde tek tek değerlendirilmiştir; boş handler uygulanmış sayılmadı. Uzak relay, bölge/kimlik/hesap değişimi ve TLS bypass EXCLUDED'dır.

Rehberler: [Windows](social-access-alpha.md), [Android](../../DPI-Bypass-Android/docs/social-access-alpha.md), [Linux](../../DPI-Bypass-Linux/docs/social-access-alpha.md). Mod, companion kurulumu, feedback/düzeltme, belirsizlikte devam ve durdurma/geri alma açıklanır.

Yerel çıktılar: Windows `artifacts/DPI-Bypass-Windows-Social-Alpha.zip`; Android `app/build/outputs/apk/debug/app-debug.apk`; Linux kaynakları ve gerçek ortam logları repodadır. Bunlar Alpha/geliştirme çıktılarıdır; resmi imzalı release veya otomatik kurulum değildir. Gerçek hesap/ISP etkisi ve fiziksel cihaz/driver koşulları ayrı kullanıcı kontrollü saha doğrulaması gerektirir.

Son Windows ZIP'in tüm girdilerinde CRC, gerekli çalıştırılabilirler/lisanslar/rehber ve güncel companion kayıt betiği doğrulandı. Windows ZIP, Android APK ve Linux kaynak arşivinin boyut/SHA-256 kayıtları `artifacts/social-alpha-checksums.txt` dosyasındadır. Linux kaynak paketi `build/linux-validation/source-final.tar` olarak ayrıca hazırdır.
