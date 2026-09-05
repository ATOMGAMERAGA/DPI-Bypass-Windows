namespace DpiBypass.Core.Vodafone;

public sealed class TtlFixException : Exception
{
    public TtlFixException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>Tuning knobs for <see cref="HotspotTtlFix"/>.</summary>
/// <remarks>
/// The numbers are the same ones the Linux build uses (<c>VODAFONE_TTL_GUARD</c> and
/// <c>VODAFONE_TTL_VALUE</c> in <c>src/dpibypass/constants.py</c>). They describe the
/// same network behaviour on both systems, so a value that differed between them would
/// be a bug in one of the two rather than a platform difference.
/// </remarks>
public sealed record TtlFixSettings
{
    /// <summary>
    /// Packets at or below this TTL are never touched.
    /// </summary>
    /// <remarks>
    /// This has to stay above the highest TTL any decoy strategy emits, or the fix
    /// would rewrite the very packets whose early expiry makes the bypass work.
    /// <c>HotspotTtlFixTests</c> fails the build if a new strategy ever crosses it.
    /// </remarks>
    public const int DefaultGuard = 32;

    /// <summary>
    /// 65 leaves exactly 64 at the operator after the phone routes the packet once,
    /// which is what its own traffic looks like.
    /// </summary>
    public const byte DefaultTimeToLive = 65;

    /// <summary>How many networks the mode may be remembered for.</summary>
    public const int MaxNetworks = 10;

    public static readonly TtlFixSettings Default = new();

    public byte TimeToLive { get; init; } = DefaultTimeToLive;

    public int Guard { get; init; } = DefaultGuard;

    /// <summary>Drop outbound IPv6 on the shared adapter so the operator sees one source.</summary>
    /// <remarks>
    /// The Linux build turns IPv6 off on the interface through sysctl for the same
    /// reason: tethering hands the laptop its own global IPv6 address, so one subscriber
    /// shows up as two distinct sources whatever the hop limit says. Dropping the
    /// packets rather than unbinding the protocol leaves nothing behind when the rule
    /// goes away, which is the difference that matters on a machine that crashes.
    /// </remarks>
    public bool DropIPv6 { get; init; } = true;

    public void Validate()
    {
        if (Guard is < 1 or > 254)
        {
            throw new TtlFixException($"Koruma eşiği 1-254 aralığında olmalı (verilen: {Guard}).");
        }

        ValidateTimeToLive(TimeToLive, Guard);
    }

    /// <summary>
    /// Rejects a TTL the rewrite must not be given, with the reason it must not.
    /// </summary>
    /// <remarks>
    /// Takes an <see cref="int"/> rather than a <see cref="byte"/> so a number typed into
    /// a settings field is refused with a message instead of wrapping around into a legal
    /// value on the way in.
    /// </remarks>
    /// <exception cref="TtlFixException">The value is outside the usable range.</exception>
    public static void ValidateTimeToLive(int value, int guard = DefaultGuard)
    {
        if (value <= guard || value > 255)
        {
            throw new TtlFixException(
                $"TTL {guard + 1}-255 aralığında olmalı (verilen: {value}). "
                    + "Koruma eşiğinin altındaki bir değer atlatma yöntemlerini bozar.");
        }
    }

    /// <summary>
    /// Reads a stored TTL, falling back to the default rather than refusing to start.
    /// </summary>
    /// <remarks>
    /// A settings file is editable, and a value out of range there must not be the
    /// reason the mode silently does nothing. The caller writes the corrected number
    /// back, which is what the Linux daemon does with <c>vodafone_ttl</c>.
    /// </remarks>
    public static byte CoerceTimeToLive(int value)
        => value > DefaultGuard && value <= 255 ? (byte)value : DefaultTimeToLive;
}
