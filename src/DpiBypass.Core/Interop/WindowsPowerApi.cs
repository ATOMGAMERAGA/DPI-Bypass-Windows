using System.Runtime.InteropServices;

namespace DpiBypass.Core.Interop;

/// <summary>Reads and writes the active plan's documented wireless power-saving value.</summary>
public static class WindowsPowerApi
{
    private static readonly Guid WirelessSettings = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid PowerSavingMode = new("12bbebe6-58d6-4636-95bb-3217ef867c1a");

    public const uint MaximumPerformance = 0;

    public static bool TryReadAcWirelessPowerSaving(out uint value)
    {
        value = 0;
        if (!OperatingSystem.IsWindows() || !TryGetActiveScheme(out var scheme))
        {
            return false;
        }

        var subgroup = WirelessSettings;
        var setting = PowerSavingMode;
        return PowerReadACValueIndex(0, ref scheme, ref subgroup, ref setting, out value) == 0;
    }

    public static bool TrySetAcWirelessPowerSaving(uint value)
    {
        if (!OperatingSystem.IsWindows() || !TryGetActiveScheme(out var scheme))
        {
            return false;
        }

        var subgroup = WirelessSettings;
        var setting = PowerSavingMode;
        return PowerWriteACValueIndex(0, ref scheme, ref subgroup, ref setting, value) == 0
            && PowerSetActiveScheme(0, ref scheme) == 0
            && TryReadAcWirelessPowerSaving(out var readBack)
            && readBack == value;
    }

    private static bool TryGetActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        if (PowerGetActiveScheme(0, out var pointer) != 0 || pointer == 0)
        {
            return false;
        }

        try
        {
            scheme = Marshal.PtrToStructure<Guid>(pointer);
            return scheme != Guid.Empty;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(
        nint rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid settingGuid,
        out uint valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        nint rootPowerKey,
        ref Guid schemeGuid,
        ref Guid subgroupGuid,
        ref Guid settingGuid,
        uint valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(nint userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
