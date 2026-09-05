using System.Xml.Linq;
using Xunit;

namespace DpiBypass.Tests;

public sealed class DeferredPageTests
{
    [Fact]
    public void StartupKeepsTheDashboardAndLogAvailableAndDefersTheFourOtherPages()
    {
        var document = XDocument.Load(RepoFiles.MainWindowXaml);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace infra = "clr-namespace:DpiBypass.App.Infrastructure";
        var tabs = document.Descendants(ns + "TabItem").ToArray();
        Assert.Equal(6, tabs.Length);
        Assert.Null(tabs[0].Element(infra + "DeferredTabContent.Template"));
        Assert.Null(tabs[5].Element(infra + "DeferredTabContent.Template"));
        foreach (var tab in tabs.Skip(1).Take(4))
        {
            var template = Assert.Single(tab.Elements(infra + "DeferredTabContent.Template"));
            Assert.Single(Assert.Single(template.Elements(ns + "DataTemplate")).Elements());
            Assert.All(tab.Elements(), child => Assert.True(
                child.Name == ns + "TabItem.Header" || child == template,
                "A deferred tab must not also construct eager content."));
        }
    }
}
