using System.Net.NetworkInformation;

namespace DpiBypass.Core.Network;

/// <summary>
/// The NIC changes this build knows about, and the rules for offering them.
/// </summary>
/// <remarks>
/// <para>
/// Every entry is a standardized NDIS keyword with a documented meaning, matched on
/// <c>RegistryKeyword</c> and written only with a value the driver itself lists in
/// <c>ValidRegistryValues</c>. Display names and display values are never read: Windows
/// localises both, and a build that matched on them would change a different setting on
/// Turkish Windows than on English.
/// </para>
/// <para>
/// Being in this catalogue is not a claim that a setting helps. It is a claim that the
/// setting has a documented mechanism by which it could plausibly move the number being
/// measured, which is what earns it a paired benchmark. Whether it actually helps on
/// this machine is decided by that benchmark and by nothing else.
/// </para>
/// <para>
/// Deliberately absent: every checksum offload keyword. Microsoft's guidance is that
/// "Address Checksum Offloads should ALWAYS be enabled no matter what workload or
/// circumstance", and they are a prerequisite for RSS, RSC and LSO, so this build will
/// not turn one off under any circumstances.
/// </para>
/// </remarks>
public static class AdapterInterventionCatalog
{
    /// <summary>Standardized keywords for the checksum offloads, which are never touched.</summary>
    public static readonly IReadOnlySet<string> ForbiddenKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "*IPChecksumOffloadIPv4",
        "*TCPChecksumOffloadIPv4",
        "*TCPChecksumOffloadIPv6",
        "*UDPChecksumOffloadIPv4",
        "*UDPChecksumOffloadIPv6",
        "*TCPUDPChecksumOffloadIPv4",
        "*TCPUDPChecksumOffloadIPv6",
    };

    public const string InterruptModerationKeyword = "*InterruptModeration";
    public const string RscIPv4Keyword = "*RscIPv4";
    public const string RscIPv6Keyword = "*RscIPv6";
    public const string RssKeyword = "*RSS";
    public const string EeeKeyword = "*EEE";
    public const string LsoIPv4Keyword = "*LsoV2IPv4";
    public const string LsoIPv6Keyword = "*LsoV2IPv6";

    public const string SelectiveSuspendProperty = "SelectiveSuspend";
    public const string D0PacketCoalescingProperty = "D0PacketCoalescing";
    public const string DeviceSleepOnDisconnectProperty = "DeviceSleepOnDisconnect";

    /// <summary>Advanced-property keywords this build is allowed to write, and nothing else.</summary>
    public static readonly IReadOnlyList<string> WritableKeywords =
    [
        InterruptModerationKeyword,
        RscIPv4Keyword,
        RscIPv6Keyword,
        RssKeyword,
        EeeKeyword,
        LsoIPv4Keyword,
        LsoIPv6Keyword,
    ];

    /// <summary>
    /// Power-management properties this build is allowed to write: none.
    /// </summary>
    /// <remarks>
    /// Both keywords this build once wrote fail the only test that matters for a
    /// steady-state round-trip experiment - whether the experiment could see the change
    /// at all. NDIS selective suspend puts an idle adapter into a low-power state after a
    /// documented idle threshold, so its effect lands on the first packet after a long
    /// gap, and a probe series that never stops producing traffic never produces one. D0
    /// packet coalescing batches broadcast and multicast receive indications, which is
    /// not the path a unicast game packet takes at all. Keeping either on the strength of
    /// a steady-state A/B result would be keeping it on the strength of noise, so neither
    /// is offered. <see cref="RestorablePowerProperties"/> is deliberately wider so a
    /// machine carrying an older snapshot still gets its original values back.
    /// </remarks>
    public static readonly IReadOnlyList<string> WritablePowerProperties = [];

    /// <summary>
    /// Power-management properties this build is allowed to put back.
    /// </summary>
    /// <remarks>
    /// Wider than what it may write, and deliberately so. An earlier build turned
    /// <c>DeviceSleepOnDisconnect</c> off as a latency candidate, which it is not - the
    /// keyword governs what the adapter does when the cable is unplugged, not what it
    /// does while a game is running. It is no longer offered, but a machine carrying a
    /// snapshot from that build still has to be able to get its original value back.
    /// </remarks>
    public static readonly IReadOnlyList<string> RestorablePowerProperties =
    [
        SelectiveSuspendProperty,
        D0PacketCoalescingProperty,
        DeviceSleepOnDisconnectProperty,
    ];

    private static readonly Dictionary<string, InterventionDescriptor> Descriptors = new(StringComparer.OrdinalIgnoreCase)
    {
        [InterruptModerationKeyword] = new InterventionDescriptor
        {
            Id = "nic.interrupt-moderation.off",
            Title = "Interrupt Moderation kapalı",
            Mechanism = "Sürücü, paket geldiğinde hemen kesme üretmek yerine daha fazla paket veya zaman "
                + "aşımı bekler; kapalıyken paket başına kesme üretilir ve CPU maliyeti artabilir.",
            Scope = LatencyTrafficScope.All,
            Risk = InterventionRisk.Moderate,
            Cost = InterventionCost.Cpu,
            // Windows exposes no operational query for interrupt moderation, so the only
            // way to know the driver is running with the new value is to restart it.
            MayNeedRestart = true,
            SettlingTime = TimeSpan.FromMilliseconds(1500),
            Reference = "learn.microsoft.com/windows-hardware/drivers/network/interrupt-moderation",
        },
        [RscIPv4Keyword] = new InterventionDescriptor
        {
            Id = "nic.rsc.ipv4.off",
            Title = "Receive Segment Coalescing (IPv4) kapalı",
            Mechanism = "RSC, aynı akışa ait paketleri işletim sistemine vermeden önce birleştirir. "
                + "Microsoft düşük gecikmeli/düşük hacimli iş yüklerinde kapalı denenmesini önerir.",
            Scope = LatencyTrafficScope.Tcp,
            Risk = InterventionRisk.Low,
            Cost = InterventionCost.Cpu,
            SettlingTime = TimeSpan.FromMilliseconds(1000),
            Reference = "learn.microsoft.com/windows-server/networking/technologies/network-subsystem/net-sub-choose-nic",
        },
        [RscIPv6Keyword] = new InterventionDescriptor
        {
            Id = "nic.rsc.ipv6.off",
            Title = "Receive Segment Coalescing (IPv6) kapalı",
            Mechanism = "IPv6 için RSC birleştirmesini kapatır; IPv4 karşılığı ile aynı mekanizma.",
            Scope = LatencyTrafficScope.Tcp,
            Risk = InterventionRisk.Low,
            Cost = InterventionCost.Cpu,
            SettlingTime = TimeSpan.FromMilliseconds(1000),
            Reference = "learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-rsc",
        },
        [RssKeyword] = new InterventionDescriptor
        {
            Id = "nic.rss.on",
            Title = "Receive Side Scaling açık",
            Mechanism = "RSS, gelen paket işlemesini birden çok mantıksal işlemciye dağıtır. "
                + "Yanlışlıkla kapatılmış çok çekirdekli kablolu sistemlerde tek çekirdek darboğazını kaldırır.",
            Scope = LatencyTrafficScope.All,
            Risk = InterventionRisk.Low,
            Cost = InterventionCost.None,
            MayNeedRestart = true,
            SettlingTime = TimeSpan.FromMilliseconds(2000),
            Reference = "learn.microsoft.com/windows-server/networking/technologies/network-subsystem/net-sub-choose-nic",
        },
        [EeeKeyword] = new InterventionDescriptor
        {
            Id = "nic.eee.off",
            Title = "Energy Efficient Ethernet kapalı",
            Mechanism = "IEEE 802.3az, hat boştayken bağlantıyı düşük güç durumuna alır; "
                + "uyanma süresi ilk paketlerde gecikme olarak görünebilir.",
            Scope = LatencyTrafficScope.All,
            Risk = InterventionRisk.Low,
            Cost = InterventionCost.Power,
            // No operational query either; 802.3az state is not reported by NetAdapter.
            MayNeedRestart = true,
            SettlingTime = TimeSpan.FromMilliseconds(1500),
            Reference = "learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management",
        },
        [LsoIPv4Keyword] = new InterventionDescriptor
        {
            Id = "nic.lso.ipv4.off",
            Title = "Large Send Offload v2 (IPv4) kapalı",
            Mechanism = "LSO, büyük TCP bloklarının parçalanmasını karta devreder. Yalnız toplu gönderim "
                + "sırasında etkilidir; oyun paketleri zaten MTU altındadır.",
            Scope = LatencyTrafficScope.Tcp,
            Risk = InterventionRisk.Moderate,
            Cost = InterventionCost.Cpu,
            SettlingTime = TimeSpan.FromMilliseconds(1000),
            Reference = "learn.microsoft.com/windows-server/networking/technologies/hpn/hpn-hardware-only-features",
        },
        [LsoIPv6Keyword] = new InterventionDescriptor
        {
            Id = "nic.lso.ipv6.off",
            Title = "Large Send Offload v2 (IPv6) kapalı",
            Mechanism = "IPv6 için LSO parçalama devrini kapatır; IPv4 karşılığı ile aynı mekanizma.",
            Scope = LatencyTrafficScope.Tcp,
            Risk = InterventionRisk.Moderate,
            Cost = InterventionCost.Cpu,
            SettlingTime = TimeSpan.FromMilliseconds(1000),
            Reference = "learn.microsoft.com/windows-server/networking/technologies/hpn/hpn-hardware-only-features",
        },
        [SelectiveSuspendProperty] = new InterventionDescriptor
        {
            Id = "nic.power.selective-suspend.off",
            Title = "Seçmeli askıya alma kapalı",
            Mechanism = "NDIS, boşta kalan bağdaştırıcıyı düşük güç durumuna alır (varsayılan eşik ~5 sn). "
                + "Etkisi sürekli trafikte değil, uzun boşluktan sonraki ilk pakette görülür.",
            Scope = LatencyTrafficScope.All,
            Risk = InterventionRisk.Low,
            Cost = InterventionCost.Power,
            AffectsSteadyStateRtt = false,
            SettlingTime = TimeSpan.FromMilliseconds(1000),
            Reference = "learn.microsoft.com/windows-hardware/drivers/network/standardized-inf-keywords-for-ndis-selective-suspend",
        },
        [D0PacketCoalescingProperty] = new InterventionDescriptor
        {
            Id = "nic.power.d0-coalescing.off",
            Title = "D0 paket birleştirme kapalı",
            Mechanism = "Rastgele yayın/çoklu yayın paketlerini birleştirerek alma kesmelerini azaltır. "
                + "Tekil (unicast) oyun trafiğini doğrudan etkilemesi beklenmez.",
            Scope = LatencyTrafficScope.All,
            Risk = InterventionRisk.Low,
            Cost = InterventionCost.Power,
            AffectsSteadyStateRtt = false,
            SettlingTime = TimeSpan.FromMilliseconds(1000),
            Reference = "learn.microsoft.com/powershell/module/netadapter/set-netadapterpowermanagement",
        },
    };

    /// <summary>The metadata for one property, or a neutral default for an unknown one.</summary>
    public static InterventionDescriptor DescriptorFor(string propertyName) =>
        Descriptors.TryGetValue(propertyName, out var descriptor)
            ? descriptor
            : new InterventionDescriptor
            {
                Id = $"nic.unknown.{propertyName.Trim('*').ToLowerInvariant()}",
                Title = propertyName,
                Mechanism = "Bilinmeyen sürücü ayarı.",
            };

    /// <summary>
    /// The candidates worth measuring on this adapter for this target.
    /// </summary>
    /// <remarks>
    /// Three filters, in order: the driver has to offer the keyword and accept the value;
    /// the setting has to be able to affect the transport being measured; and the change
    /// has to actually be a change, because a property already sitting where we would put
    /// it has nothing to teach a benchmark.
    /// </remarks>
    public static IReadOnlyList<LatencyOptimizationCandidate> Build(
        AdapterLatencyCapability adapter,
        LatencyCandidateContext context)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);

        if (!adapter.IsEligible)
        {
            return [];
        }

        var candidates = new List<LatencyOptimizationCandidate>();

        // Interrupt moderation is the one keyword with a documented effect on measured
        // round-trip time, so it is offered on any eligible adapter rather than being
        // arbitrarily limited to Ethernet.
        AddKeyword(candidates, adapter, context, InterruptModerationKeyword, "0");

        // Only when the stack says the coalescing is actually running: turning off a
        // feature the driver has already declined to use costs minutes and proves
        // nothing. A driver that does not report the state at all is still tested.
        if (adapter.RscIPv4Operational != false)
        {
            AddKeyword(candidates, adapter, context, RscIPv4Keyword, "0");
        }

        if (adapter.RscIPv6Operational != false)
        {
            AddKeyword(candidates, adapter, context, RscIPv6Keyword, "0");
        }

        AddKeyword(candidates, adapter, context, EeeKeyword, "0");

        // RSS is only worth turning on where it has processors to spread work across,
        // and only on a wired card: a wireless driver exposing the keyword is not a
        // promise that its hardware implements receive queues.
        if (!context.IsWireless
            && adapter.AdapterType == NetworkInterfaceType.Ethernet
            && context.ProcessorCount >= 4
            && adapter.RssEnabled != true)
        {
            AddKeyword(candidates, adapter, context, RssKeyword, "1");
        }

        // LSO only ever touches bulk sending, so it is never offered for an idle-latency
        // run where by construction there is no large block to segment - and today that is
        // every run: the loaded lane measures a link and a QoS policy, not adapter
        // keywords, so nothing currently sets IncludeThroughputSensitive. The entries stay
        // because the restore path needs the keyword in the writable list to be able to
        // put an LSO value back, and because a loaded-lane NIC pass would use them
        // unchanged. Neither is a claim that this build measures them.
        if (context.IncludeThroughputSensitive)
        {
            if (adapter.LsoV2IPv4Enabled != false)
            {
                AddKeyword(candidates, adapter, context, LsoIPv4Keyword, "0");
            }

            if (adapter.LsoV2IPv6Enabled != false)
            {
                AddKeyword(candidates, adapter, context, LsoIPv6Keyword, "0");
            }
        }

        return candidates;
    }

    private static void AddKeyword(
        List<LatencyOptimizationCandidate> candidates,
        AdapterLatencyCapability adapter,
        LatencyCandidateContext context,
        string keyword,
        string desiredValue)
    {
        if (ForbiddenKeywords.Contains(keyword))
        {
            throw new InvalidOperationException($"'{keyword}' bu uygulama tarafından hiçbir koşulda değiştirilmez.");
        }

        var property = adapter.AdvancedProperties.FirstOrDefault(entry =>
            string.Equals(entry.RegistryKeyword, keyword, StringComparison.OrdinalIgnoreCase));

        if (property is null
            || !property.ValidRegistryValues.Contains(desiredValue, StringComparer.OrdinalIgnoreCase)
            || property.RegistryValues.SequenceEqual([desiredValue], StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var descriptor = DescriptorFor(keyword);
        if (!IsRelevant(descriptor, context))
        {
            return;
        }

        candidates.Add(new LatencyOptimizationCandidate
        {
            Kind = LatencySettingKind.AdvancedProperty,
            PropertyName = property.RegistryKeyword,
            OriginalValues = [.. property.RegistryValues],
            DesiredValues = [desiredValue],
            Descriptor = descriptor,
            Description = descriptor.Title,
        });
    }

    private static bool IsRelevant(InterventionDescriptor descriptor, LatencyCandidateContext context)
    {
        // A change a continuously probing experiment could not see is never offered to
        // one: the run would cost minutes and produce a verdict about nothing.
        if (!descriptor.AffectsSteadyStateRtt)
        {
            return false;
        }

        if (!descriptor.IsRelevantTo(context.EffectiveScope))
        {
            return false;
        }

        // A change that costs battery is not offered on battery unless the user said so.
        return context.AllowPowerCost
            || !descriptor.Cost.HasFlag(InterventionCost.Power)
            || context.Power != PowerSource.Battery;
    }
}
