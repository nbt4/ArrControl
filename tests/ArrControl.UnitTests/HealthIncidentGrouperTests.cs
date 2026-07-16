using ArrControl.Application.Health;
using ArrControl.Application.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class HealthIncidentGrouperTests
{
    [Fact]
    public void Related_sources_share_remediation_group_and_preserve_source_details()
    {
        var groups = HealthIncidentGrouper.Group("sonarr",
        [
            new(2, "DownloadClientCheck", "warning", "Client is unavailable.",
                new Uri("https://wiki.servarr.com/sonarr/system#download-clients")),
            new(1, "ImportListCheck", "error", "Import list is unavailable.",
                new Uri("https://wiki.servarr.com/sonarr/system#download-clients")),
        ]);

        var group = Assert.Single(groups);
        Assert.Equal("error", group.Severity);
        Assert.Equal("https://wiki.servarr.com/sonarr/system#download-clients", group.RemediationUrl);
        Assert.Equal(2, group.Sources.Count);
        Assert.Contains(group.Sources, value => value.ProviderIssueId == 1
            && value.Source == "ImportListCheck" && value.Message == "Import list is unavailable.");
    }

    [Fact]
    public void Message_changes_do_not_change_the_stable_group_key()
    {
        var first = HealthIncidentGrouper.Group("radarr",
            [new(1, "IndexerCheck", "warning", "first", null)]).Single();
        var second = HealthIncidentGrouper.Group("radarr",
            [new(7, " indexercheck ", "error", "different", null)]).Single();

        Assert.Equal(first.GroupKey, second.GroupKey);
        Assert.NotEqual(first.Sources.Single().SourceKey, second.Sources.Single().SourceKey);
    }

    [Fact]
    public void Unsafe_remediation_links_are_discarded()
    {
        var group = HealthIncidentGrouper.Group("sonarr",
            [new(1, "SecurityCheck", "unexpected", null, new Uri("javascript:alert(1)"))]).Single();

        Assert.Null(group.RemediationUrl);
        Assert.Equal("unknown", group.Severity);
    }

    [Fact]
    public void Oversized_source_snapshot_is_rejected()
    {
        var issues = Enumerable.Range(0, HealthIncidentLimits.MaximumSourcesPerSnapshot + 1)
            .Select(id => new ProviderHealthIssue(id, $"source-{id}", "warning", null, null)).ToArray();

        Assert.Throws<ArgumentException>(() => HealthIncidentGrouper.Group("sonarr", issues));
    }
}
