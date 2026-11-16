# PocketPad – İlerleme

Son güncelleme: 2026-11-16

## Ortam

| Bileşen | Durum |
|---|---|
| .NET SDK | 10.0.401 (ayrıca 9.0.307) |
| Workload | `maui-android` 10.0.110 (workload set 10.0.403) |
| Android SDK | `%LOCALAPPDATA%\Android\Sdk` – platform `android-36`, build-tools 36.0.0 |
| JDK | Eclipse Adoptium 17.0.14 |
| iOS / MacCatalyst / Windows MAUI | Yüklü değil, gerekmiyor (yalnızca Android hedefleniyor) |

Derleme: `dotnet build PocketPad.slnx` → **0 uyarı, 0 hata**. Android APK üretiliyor
(`src/PocketPad.Mobile/bin/Debug/net10.0-android/com.pocketpad.mobile-Signed.apk`).
Testler: `dotnet test tests/PocketPad.Tests` → **130/130 başarılı** (Aşama 3 sonrası, art arda 4 koşuda kararlı).

Derlenemeyen ortam bağımlılığı yok. Not: MAUI şablonunun ilk restore'u iOS/MacCatalyst/Windows hedefleri
yüzünden başarısız oldu; proje yalnızca `net10.0-android` olacak şekilde düzenlendi.

## Yapı

```
PocketPad.slnx
src/
  PocketPad.Shared/    net10.0 – protokol, eşleştirme modelleri, WSS kanalı, platformdan bağımsız istemci
  PocketPad.Desktop/   net10.0-windows – WPF + Kestrel WSS sunucusu, tepsi, eşleştirme/onay
  PocketPad.Mobile/    net10.0-android – .NET MAUI arayüzü, QR tarama, SecureStorage
tests/
  PocketPad.Tests/     net10.0-windows – xUnit (birim + gerçek TLS WebSocket entegrasyon testleri)
docs/
```

## Aşama 1 – Temel mimari ✅

- **Shared**
  - Sürümlü zarf: `{"v":1,"type":"mouse.move","payload":{...}}` (`MessageEnvelope`, `MessageSerializer`).
  - Mesajlar: `MouseMove`, `MouseClick`, `MouseScroll`, `KeyboardText`, `KeyboardKey`, `MediaCommand`,
    `Ping/Pong`, `Disconnect`, `Error`, `PairRequest/PairResult`, `AuthRequest/AuthResult`.
  - System.Text.Json **source generator** (`ProtocolJsonContext`) – Android'de trimming güvenli.
  - Her mesajın `Validate()` metodu (aralık, enum, uzunluk, kontrol karakteri kontrolleri).
  - Gizli alan taşıyan kayıtların `ToString()`'i maskeli (token, secret, yazılan metin loglara düşmez).
  - Eşleştirme modelleri: `PairingQrPayload` (URI kodlama/çözme), `PairedServer`.
- **Desktop (WPF)**: MVVM (CommunityToolkit.Mvvm), DI (Microsoft.Extensions.DependencyInjection),
  koyu tema ana pencere, bağlantı durumu, eşleştirilmiş cihazlar, sistem tepsisi (`TrayIconService`);
  pencereyi kapatmak/küçültmek uygulamayı tepsiye gizler, gerçek çıkış tepsi menüsünden. Tek örnek (mutex).
- **Mobile (MAUI)**: yalnızca Android, koyu tema, Shell sekmeleri: Cihazlar, Touchpad, Klavye, Medya;
  her sayfada bağlantı göstergesi (`ConnectionBadge`), MVVM + DI.

## Aşama 2 – Güvenli ve kalıcı WebSocket bağlantısı ✅ (otomatik testlerle doğrulandı)

### Windows
- Kestrel WSS sunucusu WPF süreci içinde (kullanıcı oturumunda), varsayılan port **45900**, yol `/pocketpad/ws`.
- TLS 1.2/1.3, kendinden imzalı **ECDSA P-256** sertifika (`CertificateStore`): ilk açılışta üretilir,
  sonra yeniden kullanılır; süresi bitmeye 30 gün kala yenilenir.
- PFX (private key dahil) diskte yalnızca **DPAPI (CurrentUser)** ile şifreli: `%LOCALAPPDATA%\PocketPad\server-certificate.bin`.
- Eşleştirilmiş cihazlar: DPAPI ile şifreli `paired-devices.bin`; cihaz secret'ının yalnızca **SHA-256 hash'i** saklanır,
  sabit zamanlı karşılaştırma yapılır.
- QR (tek kullanımlık, varsayılan 2 dk geçerli): sunucu ID, IP, port, sertifika SHA-256 fingerprint, tek kullanımlık token,
  protokol sürümü, son geçerlilik. Yeni QR üretmek / iptal / kullanım / süre dolumu eski kodu geçersiz kılar;
  kullanılan veya süresi dolan kod ekrandan kaldırılır.
- İlk bağlantıda kullanıcıya onay penceresi (`ApprovalWindow`): yalnızca "İzin ver" tıklaması onaydır;
  60 sn içinde yanıt yoksa veya telefon koparsa pencere kapanır ve istek reddedilir.
- Arayüz: aktif bağlantılar (doğrulanıyor / onay bekliyor / kontrol ediyor), eşleştirilmiş cihazlar (kaldırma ile anında yetki iptali),
  QR için yerel IP seçimi, sertifika parmak izi, son alınan komut.

### Android
- QR tarama ekranı (ZXing.Net.Maui 0.10.4, kamera izni çalışma anında istenir).
- `ClientWebSocket` + **yalnızca o sokete özel** `RemoteCertificateValidationCallback`: sunucu sertifikası QR'daki
  SHA-256 fingerprint ile sabit zamanlı karşılaştırılır. Genel sertifika doğrulaması kapatılmadı.
- Onay sonrası sunucu kalıcı `DeviceId` + `DeviceSecret` verir; Android **SecureStorage**'da saklanır
  (`allowBackup=false`). Uygulama açılışında son PC'ye otomatik bağlanılır.

### Bağlantı (Shared – iki taraf da aynı `WebSocketChannel`'ı kullanır)
- Uygulama seviyesinde ping/pong heartbeat (5 sn), 15 sn sessizlikte bağlantı ölü kabul edilir.
- Tek WebSocket üzerinde sıralı gönderim kuyruğu (bounded `Channel<T>`).
- `CancellationToken` ile düzgün kapanış; önce kuyruk boşaltılır, `Disconnect` bildirimi ve close frame gönderilir.
- `PocketPadClient` otomatik yeniden bağlanma (üstel geri çekilme 1 sn → 30 sn). Kalıcı hatalarda
  (yanlış sertifika, bilinmeyen cihaz, cihaz kaldırıldı, oturum devralındı) yeniden denemez.

### Güvenlik kuralları
- Eşleştirilmemiş / kimliği doğrulanmamış bağlantıdan gelen input → `Error(Unauthorized)` + bağlantı kapatma.
- 10 sn içinde eşleşme/kimlik doğrulama yapmayan bağlantı kapatılır.
- Gelen mesajlarda boyut (8 KB), JSON, sürüm, tip, payload ve durum/yetki doğrulaması; ihlalde bağlantı kapanır.
- Yalnızca yerel ağ: loopback, 10/8, 172.16/12, 192.168/16, link-local, IPv6 ULA/link-local dışındaki adresler 403 alır.
  Uygulama firewall kuralı eklemez, UPnP/port yönlendirme yapmaz.
- Token, secret ve yazılan metin loglanmaz.
- En fazla bir aktif kontrol oturumu: başka cihaz `Busy` alır (ve otomatik olarak tekrar dener);
  aynı cihaz yeniden bağlanırsa eski (bayat) oturumunun yerini alır.

### Test kapsamı (tests/PocketPad.Tests)
- Serileştirme: tüm mesajların round-trip'i, zarf formatı, enum'ların string yazılması, geçersiz girdiler,
  boyut sınırı, ileri uyumluluk, `ToString` maskeleme, QR URI kodlama/çözme, fingerprint, yeniden bağlanma politikası.
- Masaüstü güvenliği: token tek kullanım/süre/eski QR/iptal, cihaz deposu (şifreli, yalnızca hash, kaldırma),
  sertifika (bir kez üretim + yeniden kullanım, private key'in düz metin olmaması, bozuk depoda yeniden üretim), LAN adres filtresi.
- Uçtan uca (gerçek Kestrel + TLS + WebSocket, loopback):
  onaylı eşleştirme ve input akışı · kullanıcı reddi · onay zaman aşımı · tek kullanımlık QR · eski QR · süresi dolmuş QR ·
  eşleştirmede yanlış sertifika · yeniden bağlanmada yanlış sertifika · eşleştirilmemiş cihaz / yanlış secret ·
  kimlik doğrulamadan input · handshake zaman aşımı · büyük/bozuk mesaj · tek kontrol oturumu (Busy + devralma) ·
  sunucunun düzgün kopuşu algılaması · heartbeat ile sessiz istemcinin algılanması · sunucu kapanınca istemcinin
  `Reconnecting`'e geçip yeniden bağlanması · cihaz kaldırılınca bağlantının kesilmesi · aynı cihazın bayat oturumu devralması.

### Doğrulanmayanlar (manuel test gerekli)
- **Gerçek telefon ↔ Windows bağlantısı**: test sırasında bağlı Android cihaz/emülatör yoktu (`adb devices` boş).
  APK derleniyor; kamera ile QR tarama, SecureStorage ve Android'in TLS yığını üzerinde pinning cihazda denenmedi.
- WPF uygulaması bu oturumda başlatılmadı (ilk açılışta Windows Güvenlik Duvarı izni sorabilir); arayüz yalnızca derleme ile doğrulandı.

Manuel test adımları:
1. `dotnet run --project src/PocketPad.Desktop` → Güvenlik Duvarı sorarsa yalnızca **Özel ağlar**a izin verin.
2. `dotnet build src/PocketPad.Mobile -t:Run` (USB hata ayıklama açık telefon) veya APK'yı yükleyin.
3. PC'de "Cihaz eşleştir" → telefonda Cihazlar › "QR kodu ile eşleştir" → PC'de "İzin ver".
4. Wi-Fi'ı kapatıp açın: telefonda "Yeniden bağlanıyor…" → "Bağlı"; PC'de bağlantı listesi güncellenmeli.
5. PC'de cihazı "Kaldır": telefon "Eşleştirme bilgisayardan kaldırıldı" göstermeli ve yeniden denememeli.

## Aşama 3 – Android touchpad ✅ (mantık testlerle doğrulandı, cihazda denenmedi)

### Windows
- `Input/IInputController` + `WindowsInputController`: Win32 `SendInput` (`LibraryImport`).
  - `MOUSEEVENTF_MOVE` ile göreli hareket (ABSOLUTE yok; Windows "işaretçi hassasiyetini artır" ayarı uygulanır).
  - Sol/sağ/orta tık, çift tık (`down,up,down,up` tek `SendInput` çağrısında → sistemin çift tık süresine girer), ayrı Down/Up.
  - `INPUT`/`MOUSEINPUT`/`KEYBDINPUT`/`HARDWAREINPUT` x64 uyumlu: union pointer hizalı (x64'te `INPUT` = 40 bayt, ofset 8; testle doğrulandı).
  - `SendInput` eksik ekleme yaparsa `GetLastPInvokeError` ile 10 sn'de bir uyarı loglanır, istisna fırlatılmaz, `false` döner
    (kilit ekranı, UAC/secure desktop vb.). Not: UIPI engellemesi (yönetici olarak çalışan pencere öndeyken) Windows
    tarafından hata olarak raporlanmaz; PocketPad'i yönetici olarak çalıştırmadan yükseltilmiş pencereler kontrol edilemez.
- `Input/InputDispatcher`: yalnızca **aktif ve kimliği doğrulanmış** kontrol oturumunun mesajları ulaşır
  (`ClientSession` → `SessionRegistry.IsActive`). Kesirli delta'lar taşınır (yavaş hareket kaybolmaz), kontrol oturumu değişince sıfırlanır.
  Klavye/medya/scroll mesajları henüz uygulanmıyor (yalnızca "Son komut"ta görünür).

### Android
- `Controls/TouchpadSurface` + `Platforms/Android/TouchpadSurfaceHandler`: ham çoklu dokunma `MotionEvent`'leri
  (Down/PointerDown/Move/PointerUp/Up/Cancel) px → dp çevrilerek gesture motoruna verilir; üst görünümlerin dokunuşu çalması engellenir.
- Touchpad sayfası: ekranın büyük kısmı dokunma alanı, Sol tık / Sağ tık butonları, hassasiyet kaydırıcısı (0,5×–4×, `Preferences`'ta saklanır).
  Bağlı değilken alan devre dışı ve "Bağlı değil" yazar.

### Gesture kuralları (`Shared/Input/TouchpadGestureRecognizer`, platformdan bağımsız)
| Kural | Değer |
|---|---|
| Tap toleransı (istemsiz kayma) | ≤ 8 dp |
| Tap en uzun süre | 250 ms |
| Çift dokunma penceresi / mesafesi | 300 ms / 40 dp |
| İki parmak tap en uzun süre | 300 ms |
- Tek dokunuş sol tık, **çift dokunma penceresi dolunca** gönderilir → tek ve çift tık asla birlikte üretilmez (bedeli ~300 ms gecikme).
- 8 dp'yi aşan hareket move olur; eşik aşıldığı noktadan itibaren delta üretilir (eşik mesafesi sıçrama olarak gönderilmez).
- Hareket yalnızca aynı dokunuştaki son konuma göre hesaplanır → parmak kaldırılıp başka yere konunca imleç zıplamaz.
- İkinci parmak gelince hareket durur; parmaklardan biri kalksa bile hepsi kalkana kadar imleç hareket etmez.
- Kısa ve hareketsiz iki parmak dokunuşu sağ tık; 3+ parmak veya hareketli iki parmak hiçbir şey üretmez.
- Bekleyen tek tık; başka yerde dokunma, sürükleme, iki parmak veya pencere dolması durumunda önce gönderilir.
- `Cancel` (sistem dokunuşu iptal etti) tüm durumu ve bekleyen tek tıkı siler.

### Performans (`Shared/Input/InputSender` + `MotionCoalescer`)
- UI thread yalnızca kuyruğa ekler; gönderim arka plan görevinde.
- Ardışık move'lar tek mesajda toplanır ve **~60 Hz** (16 ms) ile sınırlanır; beklerken gelen hareket bekleyen mesaja eklenir.
- Tıklamalar birleştirilmez/düşürülmez ve move'larla sıraları korunur.
- Bağlantı yokken hiçbir şey kuyruğa alınmaz/gönderilmez; gönderim başarısız olursa bekleyen eski input atılır (sonradan "tekrar oynatılmaz").

### Testler (yeni: 40)
- Gesture: tek tık gecikmesi, titreme toleransı, çift tık (tek tıksız), pencere dışı/uzak ikinci tap, move ve slop, yeniden dokunmada
  zıplamama, uzun basma, iki parmak sağ tık / uzun / hareketli, ikinci parmakla hareketin durması, 3 parmak, bekleyen tıkın
  iki parmak/sürüklemeden önce gönderilmesi, uzun ikinci basma, cancel (bekleyen tık dahil), cancel sonrası temiz başlangıç.
- Coalescer/sender: birleştirme, 60 Hz sınırı, tık sırası, sıfır/NaN, limit, bağlantı yokken gönderilmeme, hata sonrası atma.
- Windows: `INPUT` boyut/ofset, tık olay dizileri, göreli move, dispatcher kesir taşıma/sıfırlama, hata dönüşü.
- Uçtan uca: yetkili input'un controller'a sırayla ulaşması; doğrulanmamış, yanlış secret'lı ve `Busy` cihazın input'unun
  controller'a **hiç** ulaşmaması. (Testler sahte controller kullanır; gerçek fare hareket ettirilmez.)

### Gerçek cihaz test adımları (Android telefon + Windows)
Hazırlık: telefon ve PC aynı Wi-Fi'da; telefonda Geliştirici seçenekleri › USB hata ayıklama açık.
1. PC: `dotnet run --project src/PocketPad.Desktop` (yönetici olarak **değil**). Güvenlik Duvarı sorarsa yalnızca Özel ağlar.
2. Telefon: `dotnet build src/PocketPad.Mobile -t:Run` → Cihazlar › QR ile eşleştir → PC'de "İzin ver" → durum "Bağlı".
3. Touchpad sekmesi:
   - [ ] Tek parmak kaydırma imleci akıcı hareket ettiriyor; hızlı kaydırmada takılma/gecikme birikmiyor.
   - [ ] Parmağı kaldırıp ekranın başka yerine koyunca imleç zıplamıyor.
   - [ ] Tek dokunuş ~0,3 sn sonra sol tık yapıyor (masaüstünde bir simgeyi seçin).
   - [ ] Hafif titreyerek dokunmak hâlâ tık sayılıyor, imleci oynatmıyor.
   - [ ] Çift dokunuş klasör/dosya açıyor; ayrıca fazladan tek tık oluşmuyor.
   - [ ] İki parmakla kısa dokunuş sağ tık menüsünü açıyor; iki parmak varken imleç hareket etmiyor.
   - [ ] Sol/Sağ tık butonları anında çalışıyor.
   - [ ] Hassasiyet kaydırıcısı hızı değiştiriyor; uygulama yeniden açılınca değer korunuyor.
   - [ ] Dokunurken bildirim panelini çekin / uygulama değiştirin → geri dönünce hayalet tık veya hareket yok.
4. Güvenlik ve bağlantı:
   - [ ] PC'de Wi-Fi'ı/PocketPad'i kapatın: touchpad "Bağlı değil" olur, dokunuşlar birikip geri gelince **oynatılmaz**.
   - [ ] Ekranı kilitleyin (Win+L): input işlenmez, PocketPad çökmez (Debug çıktısında SendInput uyarısı görülebilir).
   - [ ] Yönetici olarak açılmış bir pencere öndeyken input'un etkisiz kaldığını gözlemleyin (UIPI – beklenen davranış).
   - [ ] PC'de cihazı "Kaldır": telefon bağlantıyı kaybeder, input gönderemez.

## Ek: QR'sız eşleştirme (LAN keşfi) + kamera düzeltmesi ✅ (testlerle doğrulandı, cihazda denenmedi)

- **Kamera:** QR okuyucu görünümü artık yalnızca kamera izni verildikten sonra oluşturuluyor (izinden önce oluşturulan
  ZXing görünümü siyah önizlemede kalıyordu); sayfadan çıkınca kamera serbest bırakılıyor.
- **LAN keşfi** (`Shared/Discovery/LanDiscovery`, `Desktop/Server/DiscoveryResponder`): telefon UDP **45901**'e yayın yapar;
  PC yalnızca **"Cihaz eşleştir" açıkken** ve yalnızca özel ağ adreslerine, QR ile aynı daveti (tek kullanımlık, 2 dk) yanıt olarak gönderir.
  Telefon PC'yi listede gösterir; seçince normal eşleştirme akışı çalışır ve **PC'de "İzin ver" onayı yine zorunludur**.
- Bilinçli ödünleşim (kullanıcı kararı, ev ağı kullanımı): keşifte sertifika parmak izi QR yerine ağdan öğrenilir (ilk kullanımda güven).
  Ağdaki biri PC'yi taklit ederse yalnızca eşleştirme anında telefonu kendine bağlayabilir; PC'nin kontrolü yine PC'deki onaya bağlıdır.
  Güvenmediğiniz ortak ağlarda QR ile eşleştirin.
- Testler (+4, toplam 134): davet yokken yanıt verilmemesi, davet varken yanıt (kaynak IP kullanımı), keşfedilen PC'nin onay
  reddi/onayı ile eşleştirilmesi, davet kullanılınca keşfin kapanması, yabancı isteklerin yok sayılması.
- APK: `artifacts/PocketPad-0.3.2-arm64.apk` (Release, yalnızca arm64; çoklu ABI'li APK 30 MB sınırını aşıyordu).

## Ek: Klavye ve medya (Windows tarafı) ✅ (testlerle doğrulandı, cihazda denenmedi)
- Kullanıcı geri bildirimi: fare çalışıyor, klavye hiç çalışmıyor → klavye/medya mesajları PC'ye ulaşıyor ama uygulanmıyordu.
- `IInputController`'a `TypeText`, `Key`, `Media` eklendi (`WindowsInputController`, `SendInput`):
  - Metin `KEYEVENTF_UNICODE` ile → klavye düzeninden bağımsız (ğüşıöç, emoji/surrogate çiftleri); `\n`/`\r\n` gerçek Enter, `\t` gerçek Tab.
  - Özel tuşlar sanal tuş kodu + tarama kodu; ok/Home/End/PgUp/PgDn/Ins/Del/Win/Menü/PrintScreen `EXTENDEDKEY` bayrağıyla
    (aksi halde numpad gibi davranır); modifier'lar Ctrl→Shift→Alt→Win sırasıyla basılır, ters sırayla bırakılır.
  - Medya: `VK_MEDIA_PLAY_PAUSE/STOP/NEXT/PREV_TRACK`, `VK_VOLUME_UP/DOWN/MUTE`.
- `InputDispatcher` Down ile basılı tutulan tuş/fare düğmelerini izler; kontrol oturumu bitince veya değişince hepsini bırakır
  (bağlantı koparsa Ctrl/Shift/sol tuş takılı kalmaz).
- Testler (+8, toplam 142): Unicode/Enter/Tab/surrogate üretimi, kontrol karakterleri, modifier sırası ve extended bayrağı,
  tüm tuş/medya eşlemeleri, dispatcher yönlendirmesi ve bırakma, uçtan uca klavye/medya + kopuşta basılı tuşun bırakılması.
- Bilinen sınırlar: Ctrl+Alt+Del `SendInput` ile gönderilemez (Windows kısıtı); yönetici olarak çalışan pencerelere yazılamaz (UIPI).

## Ek: Tasarım yenilemesi ✅ (emülatör ve WPF render ekran görüntüleriyle kontrol edildi)
- **Android:** el çizimi tutarlı SVG ikon seti (`Resources/Images`, 24×24, yuvarlak uçlu çizgiler): sekme ikonları
  (Cihazlar/Touchpad/Klavye/Medya), bilgisayar kartları (ikon karosu, "Aktif" etiketi, Bağlan/Bağlantıyı kes/çöp kutusu),
  "Bilgisayar ekle" ekranında QR ve ağ listesi satırları, touchpad'de fare ve hassasiyet ikonları, klavyede ok/geri sil/Enter
  ikon tuşları, medyada yuvarlak oynatma/ses düğmeleri. Bağlı değilken ikon düğmeler de soluklaşır.
  Yeni uygulama ikonu + açılış ekranı; Android durum çubuğu şablondaki mor renk yerine koyu tema.
- **Windows:** Segoe Fluent Icons (Win 11) / Segoe MDL2 Assets (Win 10) ile başlık, bölüm ve düğme ikonları; başlıkta logo ve
  durum etiketi; boş durumlar; alt bilgi çipleri (sunucu, adres, son komut, TLS); koyu açılır liste; onay penceresi yenilendi.
  Yeni çok boyutlu `Assets/PocketPad.ico` (uygulama, pencere ve tepsi ikonu). Pencere 960×740 (eşleştirme sırasında
  aktif bağlantılar kartı sıkışmıyor). Varlıklar derleme adıyla nitelenmiş pack URI ile yükleniyor.
- APK: `artifacts/PocketPad-0.4.0-arm64.apk`.

## Ek: Kullanıcının uygulama ikonu ✅ (emülatör başlatıcısında ve WPF render ile kontrol edildi)
- Kaynak: kullanıcının verdiği "P + imleç + wifi" ikonu (1254×1254 webp).
- **Android (adaptive icon):** görsel iki katmana ayrıldı: `appicon.png` (lacivert zemin + mor/camgöbeği parıltılar, tam taşan)
  ve `appiconfg.png` (P/imleç/wifi glifi, renk mesafesine göre alfa ile ayrıştırıldı). Glif, daire/squircle maskelerinde
  kesilmeyecek şekilde güvenli alanda (`ForegroundScale=0.82`). Açılış ekranı: glif lacivert (#050B3A) zemin üzerinde.
  Cihazlar sayfası başlığında logo.
- **Windows:** kaynak görselin köşeleri şeffaf yapılarak 16–256 px çok boyutlu `Assets/PocketPad.ico` (uygulama, pencere,
  tepsi) ve başlıktaki `Assets/logo.png`.
- APK: `artifacts/PocketPad-0.4.1-arm64.apk`.

## Ek: Masaüstü kontrolleri yenilendi ✅ (WPF render + basılma animasyonu programatik olarak denendi)
- Kullanıcı geri bildirimi: "butonlar, kaydırma çubukları kötü duruyor".
- `App.xaml`: tek buton şablonu (gradyanlı birincil, ince kenarlıklı ikincil, kare ikon ve kırmızı varyantlar) — üzerine gelince
  yumuşak aydınlanma, basınca hafif küçülme, devre dışı solukluk, yalnızca klavye odağında mavi halka; hepsi 36 px yükseklik.
- İnce (8 px), yuvarlak, oksuz koyu kaydırma çubuğu (üzerine gelince açılır, sürüklerken mavi); koyu açılır liste (açılınca mavi
  kenarlık + dönen ok + gölgeli liste, seçili öğede mavi çizgi); koyu yuvarlak ipuçları (tooltip).
- Koyu başlık çubuğu: `Views/DarkTitleBar` (DWM; Win10 1809+ koyu mod, Win 11'de başlık/kenarlık rengi uygulama renginde).
- "Kaldır" onayı Windows MessageBox yerine uygulama stilinde `ConfirmDialog` (varsayılan "Vazgeç"), `IDialogService` ile VM'den ayrık.
- Tepsi menüsü koyu (`DarkMenuRenderer`), yuvarlak seçim vurgusu.

## Aşama 4 – İki parmakla kaydırma ✅ (mantık testlerle doğrulandı; gerçek cihazda çoklu dokunma denenmedi)

### Android
- `Platforms/Android/TouchpadSurfaceHandler`: ham `MotionEvent` ACTION_DOWN / POINTER_DOWN / MOVE / POINTER_UP / UP / CANCEL.
  Parmaklar her olayda `GetPointerId(index)` ile **kalıcı pointer ID** üzerinden izlenir (index'ler parmak kalkınca kayar).
  ACTION_MOVE'daki tüm parmaklar tek parti (`PointersMoved`) olarak verilir; iki parmağın merkezi olay başına bir kez hesaplanır.
- `TouchpadGestureRecognizer` (Shared) yeni durumlar: `TwoFinger` (kaydırma/sağ tık adayı) ve `MultiTouch` (3+ parmak).
  - İki parmak: imleç hareketi yok; ortak merkez `ScrollSlop` (10 dp) aşılınca dikey kaydırma başlar, eşik mesafesi sıçrama olarak gönderilmez.
  - 1 → 2 parmak: imleç durur, kaydırma yeni merkezden ölçülür (uzaktaki ikinci parmak sıçrama üretmez).
  - 2 → 1 parmak: kalan parmak (hangisi olursa) yeni başlangıç noktası olur; parmaklar sırayla kalkarken titreme olmaması için
    hareket önce dokunma eşiğini (8 dp) aşmalı; bu aşamada tıklama üretilmez.
  - Kaydırma hiçbir zaman tık üretmez; hareketsiz kısa iki parmak dokunuşu yine sağ tık (parmaklar ayrı ayrı kalksa da).
  - 3+ parmak: ne kaydırma ne tık. 3 → 2'de kaydırma yeni merkezden devam eder.
  - `Cancel` (ACTION_CANCEL, bağlantı kopunca yüzeyin devre dışı kalması): tüm durum sıfırlanır, `GestureCancelled` olayı ile
    gönderilmemiş hareket/kaydırma kuyruğu atılır (tıklar korunur).
- Ayarlar (Touchpad sayfası, sağ üstteki ayar düğmesi; `Preferences`'ta saklanır): **İmleç hızı** (0,5–4×), **Kaydırma hızı**
  (0,25–3×), **Ters kaydırma**. Varsayılan "doğal" yön (Windows hassas touchpad'leri ve telefonlar gibi: parmaklar aşağı → sayfa
  aşağı iner → tekerlek yukarı). Dönüşüm `Shared/Input/TouchpadSettings` içinde, telefonda uygulanır; PC son değerleri alır.

### Protokol / akıcılık
- `MouseScrollMessage`: Windows tekerlek birimi (120 = bir çentik, Dy > 0 yukarı, Dx > 0 sağ), kesirli olabilir.
- `MotionCoalescer`: ardışık kaydırmalar (ve ayrıca ardışık hareketler) kuyruğun sonundaki gönderilmemiş mesajla birleşir;
  hareket+kaydırma birlikte ~60 Hz ile sınırlanır → yavaş bağlantıda birikip geç oynatılan eski kaydırma oluşmaz.
  Tık/tuş mesajları birleştirilmez, düşürülmez, sırası korunur. Bağlantı yoksa kuyruk boşaltılır.

### Windows
- `IInputController.Wheel` → `SendInput` `MOUSEEVENTF_WHEEL` (yatay için `MOUSEEVENTF_HWHEEL`); `mouseData` işaretli delta.
- `InputDispatcher` kesirli tekerlek birimlerini taşır (120'nin katı olmayan yüksek çözünürlüklü kaydırma), kontrol oturumu
  değişince/bitince sıfırlar. Yön ve hız telefonda uygulandığından PC değerleri olduğu gibi uygular.

### Testler (+26, toplam 168, art arda 3 koşuda kararlı)
1. Tek parmak hareketi (kaydırma üretmez) · 2. İki parmak aşağı/yukarı kaydırma, titreme eşiği, parmakların ayrı hareketi ·
3. 1 → 2 parmak geçişi (imleç durur, sıçrama yok) · 4. 2 → 1 parmak geçişi (kalan parmak hangisi olursa olsun, sıçramasız devam),
tekrar 2 parmağa dönüş · 5. Kaydırma sırasında/sonrasında tık yok, hareketsiz iki parmak hâlâ sağ tık, 3 parmak hiçbir şey ·
6. ACTION_CANCEL ortasında kaydırma durur, sonraki hareketler yok sayılır, yeni hareket temiz başlar.
Ek: ayar dönüşümleri (doğal/ters yön, hız, sınırlar), kaydırma birleştirme/sıra/hız sınırı, `DiscardMotion`, `MOUSEEVENTF_WHEEL`
yapısı ve işaretli `mouseData`, kesirli tekerlek taşıma ve sıfırlama, uçtan uca kaydırmanın controller'a ulaşması.
İki eski test bilinçli olarak güncellendi (2 → 1 parmakta imleç artık devam ediyor; hareketli iki parmak artık kaydırma).

### Gerçek cihaz test adımları
- [ ] Tarayıcıda uzun bir sayfada iki parmakla yukarı/aşağı kaydırın; yön ve hız beklendiği gibi, imleç kıpırdamıyor.
- [ ] Tek parmakla imleci gezdirirken ikinci parmağı koyup kaydırın, sonra bir parmağı kaldırıp imlece devam edin: sıçrama yok.
- [ ] İki parmakla kısa dokunun: sağ tık menüsü. İki parmakla kaydırıp bırakın: menü açılmamalı.
- [ ] Ayarlardan "Ters kaydırma"yı açın, kaydırma hızını değiştirin; uygulama yeniden açılınca ayarlar korunuyor.
- [ ] Kaydırırken bildirim panelini çekin veya PC'de Wi-Fi'ı kesin: geri dönünce birikmiş kaydırma "oynatılmamalı".
- APK: `artifacts/PocketPad-0.5.0-arm64.apk` (PC uygulamasının da yeniden başlatılması gerekir).

## Sıradaki işler (Aşama 5 önerisi)
- Telefonda canlı yazma (her tuşta gönderme) ve Ctrl/Alt/Shift kısayol tuşları.
- İki parmak kaydırma (scroll, `MOUSEEVENTF_WHEEL`/`HWHEEL`) ve dokun-sürükle (sol tuşu basılı tutarak sürükleme).
- Touchpad ivmesi (hıza bağlı kazanç) ve tek tık gecikmesini azaltmak için isteğe bağlı "çift dokunmayı kapat" ayarı.
- Ağ değişiminde (Android `Connectivity`) ve uygulama ön plana geldiğinde anında yeniden bağlanma.
- Windows ayarları: port, oturum açılışında otomatik başlatma, QR geçerlilik süresi; özel uygulama/tepsi ikonu.
