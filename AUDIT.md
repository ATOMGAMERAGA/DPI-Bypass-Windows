# Guvenilirlik Denetimi ve Onarim Raporu

Denetim tarihi: 2026-08-28 (America/Los_Angeles)

> Gecikme ("Ping düşürme") alt sisteminin ayrı denetimi için
> **[LATENCY-AUDIT.md](LATENCY-AUDIT.md)**, dayandığı resmî kaynaklar için
> **[LATENCY-RESEARCH.md](LATENCY-RESEARCH.md)**.

## Kapsam

Denetim; paket yakalama/yeniden yazma, IPv4/IPv6 ayristirma, QUIC siniflandirma, DNS/DoH, IPC, eszamanlilik, kaynak sinirlari, hata durumlari, testler ve paketleme akisina odaklandi. Kullanici talebi uzerine guvenlik korumasi/ACL sertlestirmesi incelenmedi ve uygulanmadi. Operator kota/Hotspot TTL ozelligi de davranissal degisiklik yapilmadan ayri tutuldu.

## Mimari Ozet

`DpiBypass.App` WPF arayuzunu ve servis kontrolunu, `DpiBypass.Core` ise WinDivert paketi, DNS vekili, DoH ve named pipe kontrol kanalini barindirir. En yuksek riskli yol, gercek paket gonderimi basladiktan sonra olusan hatalarda orijinal paketin tekrar gonderilmesi ve ayni akis icin genis cekirdek filtresinin gereksiz paket yuklemesiydi. DNS tarafinda sinirsiz gorev olusumu ve sinirsiz cache buyumesi ikinci ana kaynak riskiydi.

## Bulgular

| Kimlik | Oncelik | Guven | Durum | Bulgu ve kanit | Duzeltme / kalan is |
|---|---|---:|---|---|---|
| REL-001 | P1 | Yuksek | FIXED | Varsayilan TCP filtresi tum giden TCP payload paketlerini user-mode'a tasiyordu. `src/DpiBypass.Core/Engine/BypassEngine.cs:30` | TLS ClientHello ve bilinen HTTP metotlarini hedefleyen dar filtre ilk tercih yapildi; derleme kontrollu iki uyumluluk fallback'i korundu. |
| REL-002 | P1 | Yuksek | FIXED | Native `WinDivertSend` ve checksum yardimcisinin BOOL sonucu, ayrica gonderilen bayt sayisi denetlenmiyordu. `src/DpiBypass.Core/Interop/WinDivertHandle.cs:102` | Basari ve tam uzunluk zorunlu hale getirildi; basarisizlik cagiriciya aktariliyor. |
| REL-003 | P1 | Yuksek | FIXED | Parcalanmis enjeksiyon sonrasinda hata olursa orijinal paket yeniden gonderilebiliyor, yinelenmis/bozuk akisa yol acabiliyordu. `src/DpiBypass.Core/Engine/BypassEngine.cs:414` | Gercek segment sayisi izleniyor; kismi gonderimden sonra orijinal bastiriliyor ve filtre kapatiliyor. Enjeksiyon oncesi hatada fail-open korunuyor. Event abonesi hatasi paket yolunu bozamiyor. |
| REL-004 | P1 | Yuksek | FIXED | DNS istekleri sinirsiz `Task` ve cache girisi uretebiliyordu. `src/DpiBypass.Core/Dns/DnsProxyServer.cs:20` | 128 eszamanli istek siniri, tasma icin SERVFAIL/kapama, gorev takibi ve 4096 girdilik kilitli LRU siniri eklendi. |
| REL-005 | P2 | Yuksek | FIXED | DNS cache anahtari soru ayrintilarini kaybediyor; cevap sorusu/ID'si dogrulanmiyor ve TTL'ler yaslandirilmiyordu. `src/DpiBypass.Core/Dns/DnsMessage.cs:1` | ID haric tam wire anahtar, QR/opcode/ID/soru dogrulamasi, tum bolumlerde TTL yaslandirma, sinirli stale ve isim kanoniklestirme eklendi. |
| REL-006 | P2 | Yuksek | FIXED | TCP konumu IPv6 extension header ve fragment durumlarinda yanlis hesaplanabiliyordu. `src/DpiBypass.Core/Net/TcpIpPacket.cs:49` | IPv4 fragmentleri reddedildi; sinirli IPv6 extension zinciri ayristirildi, fragment/ESP/No Next Header ve kesik paketler reddedildi. |
| REL-007 | P2 | Yuksek | FIXED | QUIC v2 Initial paketleri v1 type bitleriyle siniflandiriliyor ve kaciriliyordu. `src/DpiBypass.Core/Net/QuicPacket.cs:1` | Version 1 type 0 ve RFC 9369 QUIC v2 type 1 ayri dogrulandi; Retry/VN/short-header reddedildi. |
| REL-008 | P2 | Yuksek | FIXED | Named pipe istemcisi satir sonu gondermez veya handler takilirsa sunucu yolu uzun sure bloke olabiliyordu. `src/DpiBypass.Core/Ipc/ControlProtocol.cs:28` | 16 KiB istek siniri ve tum connect/write/read/handler yolunu kapsayan 5 saniyelik timeout eklendi; server start idempotent yapildi. |
| REL-009 | P2 | Yuksek | OPEN | TLS ClientHello birden cok TCP segmentine veya TLS record'una bolunurse dar filtre/yeniden yazma yolu bunu birlestirmiyor. `src/DpiBypass.Core/Engine/BypassEngine.cs:30` | Akis basina sinirli ve zaman asimli reassembly gerekir. Mevcut davranis bu trafikte fail-open'dir. |
| REL-010 | P2 | Yuksek | OPEN | DNS-over-TCP baglanti basina tek sorgu isliyor; ayni sorgu birlestirme ve istemci EDNS UDP boyutuna gore TC fallback yok. `src/DpiBypass.Core/Dns/DnsProxyServer.cs:237` | RFC 7766 oturum dongusu, in-flight coalescing ve EDNS boyut politikasi ayri degisiklik olarak tasarlanmali. |
| REL-011 | P2 | Orta | OPEN | Proses atfi temel olarak source-port haritasina dayanir; port yeniden kullanimi/PID yarisi yanlis atif uretebilir. `src/DpiBypass.Core/Engine/ProcessPortMap.cs:1` | Akis 5-tuple ve zaman damgali sahiplik modeli gerekir. |
| REL-012 | P2 | Yuksek | FIXED | Hotspot TTL yolu checksum sonucunu kullanmiyor. `src/DpiBypass.Core/Vodafone/HotspotTtlFix.cs` | Ozellik kullanici talebiyle geri getirildi. Checksum yeniden hesaplanamazsa paket geldigi haliyle geri yazilip degistirilmeden iletiliyor; sayac `ChecksumFailures` ile gorunur. IPv4 baslik checksum'i TTL'yi kapsadigi icin aksi hâlde paket bir sonraki atlamada dusurulurdu. |

## Yanlis Pozitif Kontrolleri

- UDP receive buffer yeniden kullanimi veri yarisi degildi: `buffer[..n]` yeni dizi olusturdugu icin istek gorevi paylasilan receive dizisini tutmuyor.
- DNS cache anahtarinin yalnizca qname/type/class olmasi, mevcut onarimdan sonra artik gecerli degil; ID haric tam soru wire verisi kullaniliyor.
- Release adiminin dal kosusunda atlanmasi hata degil; workflow sadece `main` push icin release olusturuyor.

## Degisen Alanlar

- WinDivert native sonuc denetimi: `src/DpiBypass.Core/Interop/WinDivertHandle.cs`
- Paket filtreleme, fail-open/fail-closed gecisleri ve QUIC yolu: `src/DpiBypass.Core/Engine/BypassEngine.cs`
- IPv4/IPv6 transport ayristirma: `src/DpiBypass.Core/Net/TcpIpPacket.cs`
- QUIC v1/v2 siniflandirma: `src/DpiBypass.Core/Net/QuicPacket.cs`
- DNS wire dogrulama/cache/TTL: `src/DpiBypass.Core/Dns/DnsMessage.cs`, `DnsProxyServer.cs`, `DohResolver.cs`
- IPC boyut ve timeout sinirlari: `src/DpiBypass.Core/Ipc/ControlProtocol.cs`, `ControlServer.cs`, `ControlClient.cs`
- Regresyon testleri: `tests/DpiBypass.Tests/DnsMessageTests.cs`, `PacketTests.cs`, `KernelFilterTests.cs`, `PacketFactory.cs`

## Dogrulama Matrisi

| Katman | Sonuc | Kanit |
|---|---|---|
| C# unit/integration testleri | PASS | GitHub Actions run #42: 242/242, 0 failed, 0 skipped. |
| PowerShell kurulum testleri | PASS | Yerel ve CI: tum senaryolar gecti. |
| XAML kaynak/palet testleri | PASS | Yerel ve CI: tum kontroller gecti. |
| Release publish | PASS | CI publish cikti dogrulandi. |
| Inno Setup | PASS | CI installer derlemesi ve artifact yuklemesi tamamlandi. |
| `git diff --check` | PASS | Whitespace hatasi yok. |
| Gercek WinDivert trafik/servis testi | NOT RUN | Ayrilmis yonetici yetkili Windows VM yoktu. |
| DNS upstream kesinti/yuk testi | NOT RUN | Kontrollu ag laboratuvari yoktu. |
| Format/analyzer/lisans taramasi | NOT RUN | Baseline arac zincirinde tanimli degildi. |

CI kanitlari:

- Run #41: https://github.com/ATOMGAMERAGA/DPI-Bypass-Windows/actions/runs/33197935304
- Run #42: https://github.com/ATOMGAMERAGA/DPI-Bypass-Windows/actions/runs/33198430552

## Kalan Riskler ve Test Plani

1. Windows 10 ve 11 VM'lerinde yonetici haklariyla servis kur/kaldir, sleep/resume ve zorunlu process termination senaryolari calistirilmali.
2. TLS ClientHello 1 bayttan baslayarak farkli segment sinirlarinda bolunmeli; bypass'in fail-open kalmasi ve yinelenmis veri uretmemesi pcap ile dogrulanmali.
3. IPv6 extension zinciri, fragment, AH, bozuk uzunluk ve jumbo payload corpus'u ile fuzz/property testi eklenmeli.
4. DNS icin 128+ paralel UDP/TCP sorgusu, yavas/bozuk DoH, cache 4096 siniri, stale suresi ve shutdown sirasinda gorev sizintisi olcmeli.
5. QUIC v1/v2 Initial, Retry, Version Negotiation ve short-header paketleri gercek capture ornekleriyle dogrulanmali.
6. Dar filtrenin WinDivert surumlerinde derlenmesi ve fallback'e gecis telemetrisi performans testiyle izlenmeli.

## Uyumluluk ve Geri Alma

- Yapilandirma semasi degismedi; kullanici ayarlari icin migration gerekmiyor.
- Dar kernel filtresi derlenemezse mevcut genis filtrelere kontrollu fallback vardir.
- Yeni sinirlar davranissal olarak 128 paralel DNS istegi, 4096 cache kaydi, 16 KiB IPC istegi ve 5 saniyelik IPC timeout uygular.
- Geri alma icin iki kod commit'i ayri tutuldu: `a90f12180d88459df59c60eac75dfc079b695531` ve `e0dd31df43601964d3834e86ff22ba10499b7319`.

## Kaynaklar

Erisim tarihi: 2026-08-28.

- WinDivert 2.2 Documentation: https://reqrypt.org/windivert-doc.html
- RFC 8484, DNS Queries over HTTPS: https://www.rfc-editor.org/rfc/rfc8484.html
- RFC 9369, QUIC Version 2: https://www.rfc-editor.org/rfc/rfc9369.html
- RFC 8200, IPv6 Specification: https://www.rfc-editor.org/rfc/rfc8200.html
- RFC 1035, Domain Names Implementation and Specification: https://www.rfc-editor.org/rfc/rfc1035.html
- RFC 7766, DNS Transport over TCP: https://www.rfc-editor.org/rfc/rfc7766.html
