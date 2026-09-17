using DpiBypass.Core.Config;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the window is allowed to hand over to the compositor, and when.
/// </summary>
/// <remarks>
/// The asymmetry these tests exist to protect: a window that is flat when it could have
/// been Mica is a slightly plainer window, and a window that is transparent when nothing
/// is drawing behind it is an application the user cannot find. So every doubt has to
/// resolve to "paint it ourselves", and each of the conditions below is one that has
/// actually produced the second outcome on somebody's machine.
/// </remarks>
public sealed class BackdropPolicyTests
{
    private static BackdropEnvironment Healthy(
        int build = 22631,
        bool composition = true,
        bool remote = false,
        bool highContrast = false,
        bool accelerated = true,
        bool transparency = true,
        bool batterySaver = false)
        => new(build, composition, remote, highContrast, accelerated, transparency, batterySaver);

    [Fact]
    public void TheSystemSettingMeansMicaOnAMachineThatCanDrawIt()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.System, Healthy());

        Assert.Equal(BackdropMaterial.Mica, decision.Material);
        Assert.False(decision.Downgraded);
        Assert.True(decision.UsesCompositor);
    }

    /// <summary>
    /// "System" is Microsoft's recommended default for a main window, which is Mica -
    /// not the glassiest material the machine happens to support.
    /// </summary>
    [Fact]
    public void TheSystemSettingNeverSilentlyPicksAcrylic()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.System, Healthy());

        Assert.NotEqual(BackdropMaterial.Acrylic, decision.Material);
    }

    [Fact]
    public void AskingForAcrylicOnACapableMachineGetsAcrylic()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Acrylic, Healthy());

        Assert.Equal(BackdropMaterial.Acrylic, decision.Material);
        Assert.False(decision.Downgraded);
    }

    [Fact]
    public void TheFlatSettingNeverTouchesTheCompositor()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Plain, Healthy());

        Assert.Equal(BackdropMaterial.None, decision.Material);
        Assert.False(decision.UsesCompositor);
        Assert.False(decision.Downgraded);
    }

    /// <summary>
    /// DWMWA_SYSTEMBACKDROP_TYPE arrived in Windows 11 22H2. Older builds accept the call
    /// and draw nothing, which is precisely the invisible-window failure.
    /// </summary>
    [Theory]
    [InlineData(19045)]  // Windows 10 22H2
    [InlineData(22000)]  // Windows 11 21H2
    [InlineData(22620)]  // one short of the build that introduced the attribute
    public void ABuildWithoutTheBackdropAttributeStaysFlat(int build)
    {
        foreach (var requested in new[] { AppearanceMode.System, AppearanceMode.Mica, AppearanceMode.Acrylic })
        {
            var decision = BackdropPolicy.Decide(requested, Healthy(build: build));

            Assert.Equal(BackdropMaterial.None, decision.Material);
            Assert.Contains(build.ToString(), decision.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFirstSupportedBuildIsAllowed()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Mica, Healthy(build: BackdropEnvironment.SystemBackdropMinimumBuild));

        Assert.Equal(BackdropMaterial.Mica, decision.Material);
    }

    [Theory]
    [InlineData(false, true, true, true, "birleştirme")]
    [InlineData(true, true, false, true, "hızlandırma")]
    public void AMachineThatCannotCompositeStaysFlat(
        bool composition,
        bool transparency,
        bool accelerated,
        bool expectNotUsed,
        string expectedWord)
    {
        var decision = BackdropPolicy.Decide(
            AppearanceMode.Mica,
            Healthy(composition: composition, transparency: transparency, accelerated: accelerated));

        Assert.Equal(BackdropMaterial.None, decision.Material);
        Assert.Contains(expectedWord, decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(expectNotUsed);
    }

    [Fact]
    public void AHighContrastThemeStaysFlat()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Acrylic, Healthy(highContrast: true));

        Assert.Equal(BackdropMaterial.None, decision.Material);
        Assert.Contains("karşıtlık", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARemoteDesktopSessionStaysFlat()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Mica, Healthy(remote: true));

        Assert.Equal(BackdropMaterial.None, decision.Material);
        Assert.Contains("uzak", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Acrylic is the transparency effect, so the switch that turns transparency off
    /// turns it off. Mica is a wallpaper tint rather than a live blur, keeps being drawn,
    /// and is therefore the right fallback - a flat window here would be giving up more
    /// than the setting asked for.
    /// </summary>
    [Fact]
    public void TurningTransparencyOffDropsAcrylicToMicaRatherThanToFlat()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Acrylic, Healthy(transparency: false));

        Assert.Equal(BackdropMaterial.Mica, decision.Material);
        Assert.True(decision.Downgraded);
        Assert.Contains("Mica", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MicaSurvivesTheTransparencySwitchBeingOff()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Mica, Healthy(transparency: false));

        Assert.Equal(BackdropMaterial.Mica, decision.Material);
        Assert.False(decision.Downgraded);
    }

    [Fact]
    public void TheEnergySaverDropsAcrylicToMica()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Acrylic, Healthy(batterySaver: true));

        Assert.Equal(BackdropMaterial.Mica, decision.Material);
        Assert.True(decision.Downgraded);
        Assert.Contains("Pil", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MicaIsUnaffectedByTheEnergySaver()
    {
        var decision = BackdropPolicy.Decide(AppearanceMode.Mica, Healthy(batterySaver: true));

        Assert.Equal(BackdropMaterial.Mica, decision.Material);
    }

    /// <summary>
    /// A user who explicitly picked a material and did not get it has to be told; one who
    /// left it on "system" got exactly what that setting promises and has nothing to be
    /// warned about.
    /// </summary>
    [Fact]
    public void OnlyAnExplicitChoiceCountsAsADowngrade()
    {
        var unsupported = Healthy(build: 22000);

        Assert.True(BackdropPolicy.Decide(AppearanceMode.Mica, unsupported).Downgraded);
        Assert.True(BackdropPolicy.Decide(AppearanceMode.Acrylic, unsupported).Downgraded);
        Assert.False(BackdropPolicy.Decide(AppearanceMode.System, unsupported).Downgraded);
    }

    /// <summary>Every outcome explains itself; an empty reason is a dead end in a bug report.</summary>
    [Fact]
    public void EveryDecisionCarriesAReason()
    {
        var environments = new[]
        {
            Healthy(),
            Healthy(build: 19045),
            Healthy(composition: false),
            Healthy(remote: true),
            Healthy(highContrast: true),
            Healthy(accelerated: false),
            Healthy(transparency: false),
            Healthy(batterySaver: true),
        };

        foreach (var environment in environments)
        {
            foreach (var mode in Enum.GetValues<AppearanceMode>())
            {
                var decision = BackdropPolicy.Decide(mode, environment);
                Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
            }
        }
    }

    /// <summary>A file written before the setting existed keeps the machine's old answer.</summary>
    [Fact]
    public void TheOldOptOutIsCarriedIntoTheNewSetting()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "DisableWindowBackdrop": true }""");

        var store = new ConfigStore(settingsPath, Path.Combine(directory.Path, "profiles.json"));
        var settings = store.Load();

        Assert.Equal(AppearanceMode.Plain, settings.Appearance);
    }

    /// <summary>And a machine that never opted out starts on the system default.</summary>
    [Fact]
    public void AFileWithoutTheSettingStartsOnTheSystemDefault()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "StartMinimised": false }""");

        var store = new ConfigStore(settingsPath, Path.Combine(directory.Path, "profiles.json"));
        var settings = store.Load();

        Assert.Equal(AppearanceMode.System, settings.Appearance);
        Assert.False(settings.DisableWindowBackdrop);
    }

    /// <summary>
    /// Picking the flat surface keeps the legacy flag in step, so an older build reading
    /// the same file does not turn the material back on.
    /// </summary>
    [Fact]
    public void ChoosingTheFlatSurfaceAlsoSetsTheLegacyFlag()
    {
        using var directory = new TempDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """{ "Appearance": 3 }""");

        var store = new ConfigStore(settingsPath, Path.Combine(directory.Path, "profiles.json"));
        var settings = store.Load();

        Assert.Equal(AppearanceMode.Plain, settings.Appearance);
        Assert.True(settings.DisableWindowBackdrop);
    }
}
