namespace AtomDpi.Core.Engine;

/// <summary>
/// The catalogue of desync recipes the auto-tuner walks through. Ordering inside
/// <see cref="AtomDpi.Core.Network.IspProfile"/> decides which ones get tried
/// first on a given operator; this class just holds the definitions.
/// </summary>
public static class StrategyLibrary
{
    public static readonly BypassStrategy Passthrough = new()
    {
        Id = "passthrough",
        Name = "Kapalı (kontrol)",
        Description = "Paketlere dokunulmaz. Ağın gerçekten engelleyip engellemediğini ölçmek için kullanılır.",
    };

    public static readonly BypassStrategy SplitSni = new()
    {
        Id = "split-sni",
        Name = "SNI ortasından böl",
        Description = "TLS ClientHello, alan adının tam ortasından iki TCP parçasına bölünür.",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.HostMiddle,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy Split2 = new()
    {
        Id = "split2",
        Name = "2. bayttan böl",
        Description = "Kayıt başlığı ikinci bayttan kesilir; klasik ve çok hafif bir yöntem.",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.Absolute,
        SplitPosition = 2,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy DisorderSni = new()
    {
        Id = "disorder-sni",
        Name = "SNI ortasından ters sırayla böl",
        Description = "Parçalar ters sırada gönderilir; sıraya bakmayan denetleyiciler akışı birleştiremez.",
        Split = SplitMode.Disorder,
        Anchor = SplitAnchor.HostMiddle,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy Disorder2 = new()
    {
        Id = "disorder2",
        Name = "2. bayttan ters sırayla böl",
        Split = SplitMode.Disorder,
        Anchor = SplitAnchor.Absolute,
        SplitPosition = 2,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy FakeTtlSplitSni = new()
    {
        Id = "fake-ttl-split-sni",
        Name = "Sahte paket (düşük TTL) + SNI bölme",
        Description = "Denetleyiciye zararsız bir ClientHello gösterilir; paket sunucuya varmadan TTL ile ölür.",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.HostMiddle,
        Fake = FakeMode.ExpiredTtl,
        FakeTtl = 4,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy FakeTtl6SplitSni = FakeTtlSplitSni with
    {
        Id = "fake-ttl6-split-sni",
        Name = "Sahte paket (TTL 6) + SNI bölme",
        FakeTtl = 6,
    };

    public static readonly BypassStrategy FakeTtl8Split2 = new()
    {
        Id = "fake-ttl8-split2",
        Name = "Sahte paket (TTL 8) + 2. bayttan bölme",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.Absolute,
        SplitPosition = 2,
        Fake = FakeMode.ExpiredTtl,
        FakeTtl = 8,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy FakeBadSeqSplitSni = new()
    {
        Id = "fake-badseq-split-sni",
        Name = "Sahte paket (geçersiz sıra no) + SNI bölme",
        Description = "Sahte paketin sıra numarası pencere dışıdır, sunucu sessizce atar.",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.HostMiddle,
        Fake = FakeMode.BadSequence,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy FakeBadSumDisorderSni = new()
    {
        Id = "fake-badsum-disorder-sni",
        Name = "Sahte paket (bozuk sağlama) + ters sıralı bölme",
        Split = SplitMode.Disorder,
        Anchor = SplitAnchor.HostMiddle,
        Fake = FakeMode.BadChecksum,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy FakeBadSumSplit2 = new()
    {
        Id = "fake-badsum-split2",
        Name = "Sahte paket (bozuk sağlama) + 2. bayttan bölme",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.Absolute,
        SplitPosition = 2,
        Fake = FakeMode.BadChecksum,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy MultiSplitSni = new()
    {
        Id = "multisplit-sni",
        Name = "Üç parçalı bölme",
        Description = "Kayıt başı ve alan adı ortası olmak üzere iki noktadan kesilir.",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.HostMiddle,
        SecondSplitPosition = 1,
        Http = HttpTricks.HostCase | HttpTricks.ExtraSpace,
    };

    public static readonly BypassStrategy OobSplitSni = new()
    {
        Id = "oob-split-sni",
        Name = "Bant dışı bayt + SNI bölme",
        Description = "Önce URG bayrağıyla tek bir bant dışı bayt gönderilir.",
        Split = SplitMode.Split,
        Anchor = SplitAnchor.HostMiddle,
        OutOfBand = true,
        Http = HttpTricks.HostCase,
    };

    public static readonly BypassStrategy FakeTtlDisorderHostEnd = new()
    {
        Id = "fake-ttl-disorder-hostend",
        Name = "Sahte paket + alan adı sonundan ters bölme",
        Split = SplitMode.Disorder,
        Anchor = SplitAnchor.HostEnd,
        Fake = FakeMode.ExpiredTtl,
        FakeTtl = 5,
        FakeCount = 2,
        Http = HttpTricks.HostCase | HttpTricks.DottedHost,
    };

    public static readonly BypassStrategy AggressiveCombo = new()
    {
        Id = "aggressive-combo",
        Name = "Agresif kombinasyon",
        Description = "İki sahte paket, üç parçalı bölme ve HTTP başlık oyunları bir arada.",
        Split = SplitMode.Disorder,
        Anchor = SplitAnchor.HostMiddle,
        SecondSplitPosition = 2,
        Fake = FakeMode.ExpiredTtl,
        FakeTtl = 3,
        FakeCount = 2,
        OutOfBand = true,
        Http = HttpTricks.HostCase | HttpTricks.ExtraSpace | HttpTricks.DottedHost,
    };

    /// <summary>Every strategy the tuner may pick, in a sensible default order.</summary>
    public static readonly IReadOnlyList<BypassStrategy> All =
    [
        FakeTtlSplitSni,
        SplitSni,
        FakeBadSeqSplitSni,
        DisorderSni,
        FakeTtl6SplitSni,
        FakeBadSumDisorderSni,
        MultiSplitSni,
        Split2,
        FakeTtl8Split2,
        Disorder2,
        FakeBadSumSplit2,
        OobSplitSni,
        FakeTtlDisorderHostEnd,
        AggressiveCombo,
        Passthrough,
    ];

    public static BypassStrategy? Find(string? id)
        => id is null ? null : All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public static BypassStrategy Default => FakeTtlSplitSni;
}
