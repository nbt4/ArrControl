using ArrControl.Application.Providers;
using ArrControl.Infrastructure.Providers;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class RecyclarrCommandContractTests
{
    [Theory]
    [InlineData("Recyclarr 7.4.1", "7.4.1")]
    [InlineData("recyclarr v8.7.0", "8.7.0")]
    public async Task Version_contract_accepts_two_evidenced_major_versions(string output, string expected)
    {
        var runner = new StubRunner(new RecyclarrProcessResult(0, output));
        var result = await Client(runner).GetVersionAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Version);
        Assert.Equal(["--version"], runner.Invocations.Single().Arguments);
        Assert.Equal(TimeSpan.FromSeconds(30), runner.Invocations.Single().Timeout);
        Assert.DoesNotContain("/config", runner.Invocations.Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_builds_only_the_bounded_official_command()
    {
        var runner = new StubRunner(
            new RecyclarrProcessResult(0, "Recyclarr 8.7.0"),
            new RecyclarrProcessResult(0, "private output is discarded"));
        var result = await Client(runner).SyncAsync(
            new RecyclarrSyncRequest("Sonarr", ["series_4k", "anime"], true), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Preview);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.Equal(["sync", "sonarr", "--instance", "series_4k", "--instance", "anime",
            "--preview", "--log", "info"], runner.Invocations[1].Arguments);
        Assert.DoesNotContain("private output", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_or_shell_like_arguments_never_reach_the_process_runner()
    {
        var runner = new StubRunner(new RecyclarrProcessResult(0, string.Empty));
        var invalidService = await Client(runner).SyncAsync(
            new RecyclarrSyncRequest("lidarr", ["music"], false), CancellationToken.None);
        var invalidInstance = await Client(runner).SyncAsync(
            new RecyclarrSyncRequest("radarr", ["movies; touch /tmp/pwned"], false), CancellationToken.None);

        Assert.False(invalidService.Success);
        Assert.False(invalidInstance.Success);
        Assert.Equal(ProviderErrorCodes.InvalidResponse, invalidInstance.ErrorCode);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task Unevidenced_major_fails_closed_and_output_is_redacted()
    {
        var processResult = new RecyclarrProcessResult(0, "Recyclarr 9.0.0 /private/config");
        var result = await Client(new StubRunner(processResult)).GetVersionAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ProviderErrorCodes.UnsupportedVersion, result.ErrorCode);
        Assert.DoesNotContain("private", processResult.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static RecyclarrCommandClient Client(IRecyclarrProcessRunner runner) =>
        new(runner, "/usr/bin/recyclarr", "/config/recyclarr");

    private sealed class StubRunner(params RecyclarrProcessResult[] results) : IRecyclarrProcessRunner
    {
        public List<RecyclarrProcessInvocation> Invocations { get; } = [];
        public Task<RecyclarrProcessResult> RunAsync(
            RecyclarrProcessInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return Task.FromResult(results[Math.Min(Invocations.Count - 1, results.Length - 1)]);
        }
    }
}
