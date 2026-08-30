using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Network;

public enum PowerSource
{
    Unknown = 0,
    Mains = 1,
    Battery = 2,
}

/// <summary>
/// Everything other than the network that can move a latency number.
/// </summary>
/// <remarks>
/// <para>
/// A paired A/B comparison assumes the only difference between the two halves is the
/// setting being tested. That assumption fails quietly when the machine got busy, the
/// laptop came off mains power, or the Wi-Fi radio dropped to a slower rate halfway
/// through - and the resulting number looks exactly like a result. Recording this
/// alongside every measurement is what lets a pair be thrown away instead.
/// </para>
/// <para>
/// Nothing identifying is kept: the access point is a short hash, never its address.
/// </para>
/// </remarks>
public sealed record LatencyEnvironment
{
    public static readonly LatencyEnvironment Unknown = new();

    /// <summary>System-wide busy time as a percentage, including kernel and DPC time.</summary>
    public double? CpuBusyPercent { get; init; }

    public PowerSource Power { get; init; } = PowerSource.Unknown;

    /// <summary>Windows' 0-100 Wi-Fi signal quality, absent on wired links.</summary>
    public int? WifiSignalQuality { get; init; }

    /// <summary>Current Wi-Fi receive PHY rate in kbit/s, absent on wired links.</summary>
    public uint? WifiRxRateKbps { get; init; }

    /// <summary>Short hash of the associated access point, for change detection only.</summary>
    public string? AccessPointHash { get; init; }

    /// <summary>Short hash of the first-hop router, so a route change invalidates a pair.</summary>
    public string? RouteHash { get; init; }

    public int InterfaceIndex { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Largest CPU-busy difference two halves of a pair may have, in points.</summary>
    public const double MaximumComparableCpuDeltaPercent = 20;

    /// <summary>Largest Wi-Fi signal-quality difference two halves may have, in points.</summary>
    public const int MaximumComparableSignalDelta = 12;

    /// <summary>Largest ratio between two halves' Wi-Fi PHY rates.</summary>
    public const double MaximumComparableRateRatio = 1.5;

    /// <summary>
    /// Whether two windows ran on a machine and a radio alike enough to be subtracted.
    /// </summary>
    /// <remarks>
    /// Unknown values are permissive on purpose: a machine that cannot report its CPU
    /// load has not told us the halves differed, and refusing every pair on a system
    /// without those counters would turn "we cannot tell" into "nothing ever works".
    /// The statistical side compensates by requiring a larger effect when the load
    /// state is unknown.
    /// </remarks>
    public bool ComparableWith(LatencyEnvironment other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (InterfaceIndex != other.InterfaceIndex)
        {
            return false;
        }

        if (!SameOrUnknown(RouteHash, other.RouteHash) || !SameOrUnknown(AccessPointHash, other.AccessPointHash))
        {
            return false;
        }

        if (Power != PowerSource.Unknown && other.Power != PowerSource.Unknown && Power != other.Power)
        {
            return false;
        }

        if (CpuBusyPercent is { } mine && other.CpuBusyPercent is { } theirs
            && Math.Abs(mine - theirs) > MaximumComparableCpuDeltaPercent)
        {
            return false;
        }

        if (WifiSignalQuality is { } signal && other.WifiSignalQuality is { } otherSignal
            && Math.Abs(signal - otherSignal) > MaximumComparableSignalDelta)
        {
            return false;
        }

        if (WifiRxRateKbps is > 0 and { } rate && other.WifiRxRateKbps is > 0 and { } otherRate)
        {
            var high = Math.Max(rate, otherRate);
            var low = Math.Min(rate, otherRate);
            if ((double)high / low > MaximumComparableRateRatio)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Why two windows were not comparable, for the log and the report.</summary>
    public string DescribeDifference(LatencyEnvironment other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (InterfaceIndex != other.InterfaceIndex)
        {
            return "bağdaştırıcı değişti";
        }

        if (!SameOrUnknown(RouteHash, other.RouteHash))
        {
            return "rota/ağ geçidi değişti";
        }

        if (!SameOrUnknown(AccessPointHash, other.AccessPointHash))
        {
            return "erişim noktası (BSSID) değişti";
        }

        if (Power != PowerSource.Unknown && other.Power != PowerSource.Unknown && Power != other.Power)
        {
            return "güç kaynağı değişti";
        }

        if (CpuBusyPercent is { } mine && other.CpuBusyPercent is { } theirs
            && Math.Abs(mine - theirs) > MaximumComparableCpuDeltaPercent)
        {
            return $"CPU yükü %{mine:F0} → %{theirs:F0} değişti";
        }

        if (WifiSignalQuality is { } signal && other.WifiSignalQuality is { } otherSignal
            && Math.Abs(signal - otherSignal) > MaximumComparableSignalDelta)
        {
            return $"Wi-Fi sinyali %{signal} → %{otherSignal} değişti";
        }

        return "Wi-Fi bağlantı hızı değişti";
    }

    private static bool SameOrUnknown(string? first, string? second)
        => first is null || second is null || string.Equals(first, second, StringComparison.Ordinal);

    internal static string? Hash(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}

public interface ILatencyEnvironmentSampler
{
    /// <summary>Reads the machine and radio state right now. Never sends anything.</summary>
    LatencyEnvironment Sample(NetworkFingerprint network);
}

/// <summary>
/// Reads CPU busy time, power source and Wi-Fi radio state from Windows.
/// </summary>
/// <remarks>
/// <c>GetSystemTimes</c> is used rather than a performance counter because it is a
/// single cheap call with no counter category to be missing or corrupted, and its
/// kernel figure already includes the DPC and interrupt time that a NIC setting is
/// most likely to move.
/// </remarks>
public sealed class WindowsLatencyEnvironmentSampler : ILatencyEnvironmentSampler
{
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();

    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;
    private bool _hasPrevious;

    public WindowsLatencyEnvironmentSampler(Action<string>? log = null) => _log = log;

    public LatencyEnvironment Sample(NetworkFingerprint network)
    {
        ArgumentNullException.ThrowIfNull(network);

        var wlan = network.IsWireless ? TryReadRadio() : null;

        return new LatencyEnvironment
        {
            CpuBusyPercent = ReadCpuBusyPercent(),
            Power = ReadPowerSource(),
            WifiSignalQuality = wlan?.SignalQuality >= 0 ? wlan.Value.SignalQuality : null,
            WifiRxRateKbps = wlan?.RxRateKbps is > 0 ? wlan.Value.RxRateKbps : null,
            AccessPointHash = LatencyEnvironment.Hash(network.Bssid),
            RouteHash = LatencyEnvironment.Hash(network.GatewayMac ?? network.GatewayAddress),
            InterfaceIndex = network.InterfaceIndex,
            At = DateTimeOffset.UtcNow,
        };
    }

    private WlanConnection? TryReadRadio()
    {
        try
        {
            return WlanApi.TryGetCurrentConnection();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.Invoke($"latency.environment: Wi-Fi durumu okunamadı ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Busy time since the previous call, or null on the first one.</summary>
    private double? ReadCpuBusyPercent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return null;
            }

            lock (_gate)
            {
                var idleTicks = ToTicks(idle);
                var kernelTicks = ToTicks(kernel);
                var userTicks = ToTicks(user);

                if (!_hasPrevious)
                {
                    _hasPrevious = true;
                    _lastIdle = idleTicks;
                    _lastKernel = kernelTicks;
                    _lastUser = userTicks;
                    return null;
                }

                // Kernel time includes idle time, so the total is kernel + user and the
                // busy share is what is left once idle is taken out of it.
                var idleDelta = idleTicks - _lastIdle;
                var totalDelta = (kernelTicks - _lastKernel) + (userTicks - _lastUser);

                _lastIdle = idleTicks;
                _lastKernel = kernelTicks;
                _lastUser = userTicks;

                if (totalDelta <= 0 || idleDelta < 0)
                {
                    return null;
                }

                return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static PowerSource ReadPowerSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return PowerSource.Unknown;
        }

        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return PowerSource.Unknown;
            }

            return status.AcLineStatus switch
            {
                0 => PowerSource.Battery,
                1 => PowerSource.Mains,
                _ => PowerSource.Unknown,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return PowerSource.Unknown;
        }
    }

    private static long ToTicks(FileTime time) => ((long)time.High << 32) | (uint)time.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public int Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
