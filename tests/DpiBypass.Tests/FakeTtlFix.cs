using DpiBypass.Core.Vodafone;

namespace DpiBypass.Tests;

/// <summary>
/// A TTL rule that records what it was asked to do instead of opening a driver.
/// </summary>
/// <remarks>
/// The real one needs WinDivert and administrator rights, so on a build machine it can
/// only ever fail. The decision this substitutes for - whether the rule should be up, on
/// which adapter, with which settings - is the part that has been wrong before and the
/// part worth pinning.
/// </remarks>
internal sealed class FakeTtlFix : IHotspotTtlFix
{
    private readonly Func<int, TtlFixSettings, Exception?>? _refuse;

    /// <param name="refuse">
    /// Returns the exception one <c>Apply</c> should fail with, or null to succeed. Lets
    /// a test reproduce the machine where the driver is missing or the process is not
    /// elevated, which is the case the card has to describe rather than hide.
    /// </param>
    public FakeTtlFix(Func<int, TtlFixSettings, Exception?>? refuse = null) => _refuse = refuse;

    public bool IsActive { get; private set; }

    public int InterfaceIndex { get; private set; }

    public TtlFixSettings Settings { get; private set; } = TtlFixSettings.Default;

    public long RewrittenPackets { get; set; }

    public long DroppedIPv6Packets { get; set; }

    /// <summary>How many times a rule was installed, successfully or not.</summary>
    public int Applies { get; private set; }

    /// <summary>How many times an installed rule was taken down.</summary>
    public int Clears { get; private set; }

    /// <summary>The adapter index of every install attempt, in order.</summary>
    public List<int> AppliedTo { get; } = [];

    public void Apply(int interfaceIndex, TtlFixSettings settings)
    {
        Applies++;
        AppliedTo.Add(interfaceIndex);

        if (_refuse?.Invoke(interfaceIndex, settings) is { } error)
        {
            IsActive = false;
            InterfaceIndex = 0;
            throw error;
        }

        IsActive = true;
        InterfaceIndex = interfaceIndex;
        Settings = settings;
    }

    public void Clear()
    {
        if (IsActive)
        {
            Clears++;
        }

        IsActive = false;
        InterfaceIndex = 0;
    }

    public void Dispose() => Clear();
}
