using PocketPad.Shared.Client;
using PocketPad.Shared.Protocol;

namespace PocketPad.Mobile.Services;

/// <summary>Turkish user-facing texts for connection states and failures.</summary>
public static class StatusText
{
    public static string ForState(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "Bağlı",
        ConnectionState.Connecting => "Bağlanıyor…",
        ConnectionState.AwaitingApproval => "Bilgisayarda onay bekleniyor…",
        ConnectionState.Reconnecting => "Yeniden bağlanıyor…",
        _ => "Bağlı değil",
    };

    public static string? ForFailure(ConnectionFailure failure, RejectReason reason) => (failure, reason) switch
    {
        (_, RejectReason.UserDenied) => "Eşleştirme bilgisayarda reddedildi.",
        (_, RejectReason.ApprovalTimeout) => "Bilgisayarda zamanında onay verilmedi.",
        (_, RejectReason.InvalidPairingToken) => "QR kodu geçersiz, kullanılmış veya süresi dolmuş. Yeni kod oluşturun.",
        (_, RejectReason.UnknownDevice or RejectReason.Unauthorized) => "Bu telefon artık eşleştirilmiş değil. Yeniden eşleştirin.",
        (_, RejectReason.DeviceRemoved) => "Eşleştirme bilgisayardan kaldırıldı.",
        (_, RejectReason.Busy) => "Başka bir cihaz şu anda bu bilgisayarı kontrol ediyor.",
        (_, RejectReason.Replaced) => "Bu cihaz başka bir bağlantıyla yeniden bağlandı.",
        (_, RejectReason.ServerShuttingDown) => "Bilgisayardaki PocketPad kapatıldı.",
        (ConnectionFailure.CertificateMismatch, _) => "Güvenlik uyarısı: bilgisayarın sertifikası eşleşmiyor. Bağlantı reddedildi.",
        (ConnectionFailure.Unreachable, _) => "Bilgisayara ulaşılamıyor. Aynı Wi-Fi ağında olduğunuzdan emin olun.",
        (ConnectionFailure.HandshakeTimeout, _) => "Bilgisayar yanıt vermedi.",
        (ConnectionFailure.ConnectionLost, _) => "Bağlantı koptu.",
        (ConnectionFailure.ProtocolError, _) => "Uyumsuz sürüm veya protokol hatası.",
        _ => null,
    };
}
