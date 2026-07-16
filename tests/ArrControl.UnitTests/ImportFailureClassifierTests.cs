using ArrControl.Application.Activity;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class ImportFailureClassifierTests
{
    [Theory]
    [InlineData("downloadclientunavailable", "ok", "downloading", "download_client_unavailable")]
    [InlineData("downloading", "ok", "importblocked", "import_blocked")]
    [InlineData("downloading", "ok", "failed", "import_failed")]
    [InlineData("downloading", "ok", "failedpending", "import_retry_pending")]
    [InlineData("downloading", "error", "importing", "upstream_error")]
    [InlineData("downloading", "warning", "importpending", "import_pending")]
    public void Queue_rules_are_ordered_exact_and_never_claim_unsupported_retry(
        string status, string trackedStatus, string trackedState, string category)
    {
        var result = ImportFailureClassifier.ClassifyQueue(status, trackedStatus, trackedState);
        Assert.Equal(category, result?.Category);
        Assert.False(result?.RetrySupported);
    }

    [Fact]
    public void Unknown_values_and_titles_do_not_trigger_a_guess()
    {
        Assert.Null(ImportFailureClassifier.ClassifyQueue("unknown", "unknown", "unknown"));
        Assert.Null(ImportFailureClassifier.ClassifyHistory("unknown"));
        Assert.Equal("download_failed",
            ImportFailureClassifier.ClassifyHistory("downloadfailed")?.Category);
    }
}
