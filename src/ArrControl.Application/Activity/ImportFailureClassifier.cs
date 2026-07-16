namespace ArrControl.Application.Activity;

public sealed record ImportFailureClassification(
    string Category,
    string Severity,
    string RuleId,
    string GuidanceKey,
    bool RetrySupported);

public static class ImportFailureClassifier
{
    public static ImportFailureClassification? ClassifyQueue(
        string status,
        string trackedStatus,
        string trackedState) =>
        (status, trackedStatus, trackedState) switch
        {
            ("downloadclientunavailable", _, _) => Failure(
                "download_client_unavailable", "error", "queue.status.download_client_unavailable",
                "import.guidance.check_download_client"),
            (_, _, "importblocked") => Failure(
                "import_blocked", "error", "queue.state.import_blocked",
                "import.guidance.review_upstream_import"),
            (_, _, "failed") => Failure(
                "import_failed", "error", "queue.state.failed",
                "import.guidance.review_upstream_import"),
            (_, _, "failedpending") => Failure(
                "import_retry_pending", "warning", "queue.state.failed_pending",
                "import.guidance.wait_or_review_upstream"),
            (_, "error", _) => Failure(
                "upstream_error", "error", "queue.tracked_status.error",
                "import.guidance.review_upstream"),
            (_, "warning", "importpending") => Failure(
                "import_pending", "warning", "queue.warning.import_pending",
                "import.guidance.wait_or_review_upstream"),
            _ => null,
        };

    public static ImportFailureClassification? ClassifyHistory(string eventType) =>
        eventType switch
        {
            "downloadfailed" => Failure(
                "download_failed", "error", "history.event.download_failed",
                "import.guidance.search_again"),
            "downloadignored" => Failure(
                "download_ignored", "warning", "history.event.download_ignored",
                "import.guidance.review_upstream"),
            _ => null,
        };

    private static ImportFailureClassification Failure(
        string category,
        string severity,
        string ruleId,
        string guidanceKey) =>
        new(category, severity, ruleId, guidanceKey, RetrySupported: false);
}
