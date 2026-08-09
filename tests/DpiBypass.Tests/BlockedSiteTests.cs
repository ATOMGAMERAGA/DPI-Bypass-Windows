using DpiBypass.Core.Config;
using DpiBypass.Core.Engine;
using Xunit;

namespace DpiBypass.Tests;

public class BlockedSiteTests
{
    private static TargetMatcher Matcher(ProtectionScope scope) => new() { Scope = scope };

    [Fact]
    public void TheBlockedSitesScopeCoversTheShippedListWhicheverProgramAsked()
    {
        var matcher = Matcher(ProtectionScope.BlockedSites);

        Assert.True(matcher.ShouldProtect("discord.com", @"C:\Program Files\Some\thing.exe"));
        Assert.True(matcher.ShouldProtect("www.roblox.com", @"C:\games\player.exe"));
        Assert.True(matcher.ShouldProtect("instagram.com", null));
    }

    [Fact]
    public void TheBlockedSitesScopeLeavesEverythingElseAlone()
    {
        var matcher = Matcher(ProtectionScope.BlockedSites);

        Assert.False(matcher.ShouldProtect("example.com", @"C:\chrome.exe"));
        Assert.False(matcher.ShouldProtect("microsoft.com", null));
    }

    [Fact]
    public void DiscordOnlyStaysNarrowerThanTheBlockedSiteList()
    {
        var matcher = Matcher(ProtectionScope.DiscordOnly);

        Assert.True(matcher.ShouldProtect("discord.com", null));
        Assert.False(matcher.ShouldProtect("roblox.com", null));
    }

    [Fact]
    public void ALearnedDomainIsProtectedFromThenOn()
    {
        var matcher = Matcher(ProtectionScope.BlockedSites);
        Assert.False(matcher.ShouldProtect("blocked.example", null));

        matcher.LearnedDomains.Add("blocked.example");

        Assert.True(matcher.ShouldProtect("blocked.example", null));
        Assert.True(matcher.ShouldProtect("cdn.blocked.example", null));
    }

    [Fact]
    public void AnExcludedDomainWinsOverEverythingIncludingSystemWideScope()
    {
        var matcher = Matcher(ProtectionScope.Everything);
        matcher.ExcludedDomains.Add("bank.example");

        Assert.False(matcher.ShouldProtect("bank.example", null));
        Assert.False(matcher.ShouldProtect("login.bank.example", null));
        Assert.True(matcher.ShouldProtect("other.example", null));
    }

    [Fact]
    public void AnExclusionAlsoOverridesTheShippedList()
    {
        var matcher = Matcher(ProtectionScope.BlockedSites);
        matcher.ExcludedDomains.Add("instagram.com");

        Assert.False(matcher.ShouldProtect("instagram.com", null));
        Assert.True(matcher.ShouldProtect("discord.com", null));
    }

    [Fact]
    public void TheShippedListNeverMatchesOnASubstring()
    {
        Assert.False(TargetMatcher.IsKnownBlockedDomain("notdiscord.com"));
        Assert.False(TargetMatcher.IsKnownBlockedDomain("discord.com.evil.example"));
        Assert.True(TargetMatcher.IsKnownBlockedDomain("cdn.discord.com"));
    }

    [Fact]
    public void TheShippedListContainsDiscordAndTheOtherFilteredSites()
    {
        Assert.Contains("discord.com", BlockedSiteCatalog.All);
        Assert.Contains("roblox.com", BlockedSiteCatalog.All);
        Assert.Equal(BlockedSiteCatalog.Discord.Length + BlockedSiteCatalog.Other.Length, BlockedSiteCatalog.All.Length);
        Assert.Equal(BlockedSiteCatalog.All.Distinct().Count(), BlockedSiteCatalog.All.Length);
    }

    // --- the overrides the discovery pass measures with ------------------------

    /// <summary>
    /// The control arm has to reach the site untouched, but only that site: every
    /// other connection on the machine must keep its protection while the probe runs.
    /// </summary>
    [Fact]
    public void TheProbePassthroughOverrideAppliesToOneHostOnly()
    {
        var matcher = Matcher(ProtectionScope.Everything);
        matcher.ProbePassthroughHost = "candidate.example";

        Assert.False(matcher.ShouldProtect("candidate.example", null));
        Assert.True(matcher.ShouldProtect("discord.com", null));
        Assert.True(matcher.ShouldProtect("anything.example", null));
    }

    /// <summary>
    /// The treatment arm measures a host that is not protected yet - that is the whole
    /// question - so it has to be forced in, or both arms would measure the same thing
    /// and nothing would ever be learned.
    /// </summary>
    [Fact]
    public void TheProbeForcedOverrideProtectsAHostTheScopeWouldNot()
    {
        var matcher = Matcher(ProtectionScope.DiscordOnly);
        Assert.False(matcher.ShouldProtect("candidate.example", null));

        matcher.ProbeForcedHost = "candidate.example";

        Assert.True(matcher.ShouldProtect("candidate.example", null));
        Assert.False(matcher.ShouldProtect("other.example", null));
    }

    [Fact]
    public void ClearingTheOverridesRestoresNormalMatching()
    {
        var matcher = Matcher(ProtectionScope.BlockedSites);
        matcher.ProbePassthroughHost = "discord.com";
        matcher.ProbeForcedHost = "other.example";

        matcher.ProbePassthroughHost = null;
        matcher.ProbeForcedHost = null;

        Assert.True(matcher.ShouldProtect("discord.com", null));
        Assert.False(matcher.ShouldProtect("other.example", null));
    }

    /// <summary>An explicit opt-out must beat a probe override, not the other way round.</summary>
    [Fact]
    public void AnExclusionStillWinsOverAProbeOverride()
    {
        var matcher = Matcher(ProtectionScope.BlockedSites);
        matcher.ExcludedDomains.Add("bank.example");
        matcher.ProbeForcedHost = "bank.example";

        Assert.False(matcher.ShouldProtect("bank.example", null));
    }

    // --- learned domain persistence -------------------------------------------

    private static LearnedDomainStore TemporaryStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"dpibypass-learned-{Guid.NewGuid():N}.json");
        return new LearnedDomainStore(path);
    }

    [Fact]
    public void LearnedDomainsSurviveAReload()
    {
        var store = TemporaryStore(out var path);

        try
        {
            Assert.True(store.Add("Blocked.Example"));
            Assert.False(store.Add("blocked.example"));

            var reloaded = new LearnedDomainStore(path);
            Assert.Contains("blocked.example", reloaded.Domains);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHostnameWithoutADotIsNotLearned()
    {
        var store = TemporaryStore(out var path);

        try
        {
            Assert.False(store.Add("localhost"));
            Assert.False(store.Add("  "));
            Assert.Empty(store.Domains);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheLearnedListIsBoundedAndDropsTheOldest()
    {
        var store = TemporaryStore(out var path);

        try
        {
            for (var i = 0; i < LearnedDomainStore.Capacity + 10; i++)
            {
                store.Add($"host{i}.example");
            }

            Assert.Equal(LearnedDomainStore.Capacity, store.Count);
            Assert.False(store.Contains("host0.example"));
            Assert.True(store.Contains($"host{LearnedDomainStore.Capacity + 9}.example"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RemovingALearnedDomainPersists()
    {
        var store = TemporaryStore(out var path);

        try
        {
            store.Add("blocked.example");
            Assert.True(store.Remove("blocked.example"));
            Assert.False(store.Remove("blocked.example"));

            Assert.Empty(new LearnedDomainStore(path).Domains);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
