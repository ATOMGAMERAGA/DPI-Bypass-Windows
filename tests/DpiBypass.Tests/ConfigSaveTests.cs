using DpiBypass.Core.Config;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What happens to a setting the app cannot write down.
/// </summary>
/// <remarks>
/// The write used to be wrapped in a bare <c>catch</c>, so a machine whose state
/// directory was read-only - a locked-down work laptop, a full disk, a backup tool
/// holding the file - showed a settings screen that silently reverted itself on every
/// launch. The setting still applies for the session; what changed is that the failure
/// is returned instead of dropped.
/// </remarks>
public sealed class ConfigSaveTests
{
    /// <summary>
    /// Blocks a write without needing file permissions, which root does not have to obey.
    /// </summary>
    /// <remarks>
    /// The atomic write goes to "&lt;path&gt;.tmp" first, so a directory sitting on that
    /// name makes the write fail the way a permission problem does - on every platform,
    /// and for an elevated process too, which is what this app always is.
    /// </remarks>
    private static void BlockWritesTo(string path) => Directory.CreateDirectory(path + ".tmp");

    private static void UnblockWritesTo(string path) => Directory.Delete(path + ".tmp", recursive: true);

    [Fact]
    public void AWriteThatLandsReportsSuccess()
    {
        using var directory = new TempDirectory();
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));

        var result = store.Save(new AppSettings { DnsMode = DnsMode.PublicResolvers });

        Assert.True(result.Succeeded);
        Assert.Equal(ConfigSaveFailure.None, result.Failure);
        Assert.Equal(DnsMode.PublicResolvers, store.Load().DnsMode);
    }

    [Fact]
    public void AWriteTheSystemRefusesIsReportedRatherThanSwallowed()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.File("settings.json");
        var store = new ConfigStore(settingsPath, directory.File("networks.json"));

        BlockWritesTo(settingsPath);
        var result = store.Save(new AppSettings { DnsMode = DnsMode.PublicResolvers });

        Assert.False(result.Succeeded);
        Assert.Equal(ConfigSaveFailure.AccessDenied, result.Failure);
        Assert.Contains("settings.json", result.Detail);
        Assert.Contains("Bu oturumda uygulandı", result.Describe());
    }

    /// <summary>
    /// A failed write never destroys the last file that did work.
    /// </summary>
    [Fact]
    public void TheLastGoodFileSurvivesAWriteThatFails()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.File("settings.json");
        var store = new ConfigStore(settingsPath, directory.File("networks.json"));

        Assert.True(store.Save(new AppSettings { DnsMode = DnsMode.EncryptedLoopback, Scope = ProtectionScope.DiscordOnly }).Succeeded);

        BlockWritesTo(settingsPath);
        Assert.False(store.Save(new AppSettings { DnsMode = DnsMode.SystemDefault, Scope = ProtectionScope.Everything }).Succeeded);
        UnblockWritesTo(settingsPath);

        // Still openable, and still holding the settings that were written successfully.
        var reloaded = store.Load();
        Assert.Equal(DnsMode.EncryptedLoopback, reloaded.DnsMode);
        Assert.Equal(ProtectionScope.DiscordOnly, reloaded.Scope);
    }

    /// <summary>
    /// Once the obstruction is gone the next write lands, and the newest preference wins.
    /// </summary>
    [Fact]
    public void TheNewestPreferenceSurvivesARunOfFailedWrites()
    {
        using var directory = new TempDirectory();
        var settingsPath = directory.File("settings.json");
        var store = new ConfigStore(settingsPath, directory.File("networks.json"));
        var settings = new AppSettings();

        BlockWritesTo(settingsPath);
        foreach (var scope in new[] { ProtectionScope.DiscordOnly, ProtectionScope.Everything, ProtectionScope.DiscordAndBrowsers })
        {
            settings.Scope = scope;
            Assert.False(store.Save(settings).Succeeded);
        }

        UnblockWritesTo(settingsPath);
        settings.Scope = ProtectionScope.DiscordOnly;
        Assert.True(store.Save(settings).Succeeded);

        Assert.Equal(ProtectionScope.DiscordOnly, store.Load().Scope);
    }

    /// <summary>A failed write leaves no half finished temporary file behind.</summary>
    [Fact]
    public void NoStrayTemporaryFileIsLeftAfterAFailedWrite()
    {
        using var directory = new TempDirectory();
        var profilesPath = directory.File("networks.json");
        var store = new ConfigStore(directory.File("settings.json"), profilesPath);

        BlockWritesTo(profilesPath);
        Assert.False(store.SaveNetworks(new AppSettings()).Succeeded);
        UnblockWritesTo(profilesPath);

        Assert.False(File.Exists(profilesPath + ".tmp"));
    }

    /// <summary>
    /// A profile being written on one thread cannot be half copied into the file another
    /// thread is serialising.
    /// </summary>
    /// <remarks>
    /// The store's lock protects the writer, not the dictionary: serialising a mutable
    /// collection while a background re-tune adds to it is a collection-modified exception
    /// in the middle of a save, which under the old bare catch became a settings file that
    /// silently did not update. The snapshot is taken under the same lock as the write.
    /// </remarks>
    [Fact]
    public async Task WritingProfilesWhileTheyChangeNeverCorruptsTheSave()
    {
        using var directory = new TempDirectory();
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));
        var settings = new AppSettings();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var churn = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                lock (settings.Networks)
                {
                    settings.Networks[$"net-{i++ % 50}"] = new NetworkProfile
                    {
                        Key = $"net-{i % 50}",
                        StrategyId = "split-sni",
                        LastVerified = DateTimeOffset.UtcNow,
                    };
                }
            }
        });

        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            ConfigSaveResult result;
            lock (settings.Networks)
            {
                result = store.SaveNetworks(settings);
            }

            if (!result.Succeeded)
            {
                failures++;
            }
        }

        await churn;

        Assert.Equal(0, failures);

        // The file still parses, which is the property that matters: an interrupted write
        // must never be what the next launch reads.
        Assert.NotNull(store.Load().Networks);
    }
}
