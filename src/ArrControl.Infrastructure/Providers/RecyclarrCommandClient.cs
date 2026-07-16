using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ArrControl.Application.Providers;

namespace ArrControl.Infrastructure.Providers;

public sealed record RecyclarrProcessInvocation(
    string ExecutablePath,
    string ConfigurationDirectory,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout)
{
    public override string ToString() =>
        "RecyclarrProcessInvocation { ExecutablePath = [REDACTED], ConfigurationDirectory = [REDACTED], Arguments = [REDACTED] }";
}

public sealed record RecyclarrProcessResult(int ExitCode, [property: JsonIgnore] string Output)
{
    public override string ToString() => $"RecyclarrProcessResult {{ ExitCode = {ExitCode}, Output = [REDACTED] }}";
}

public interface IRecyclarrProcessRunner
{
    Task<RecyclarrProcessResult> RunAsync(
        RecyclarrProcessInvocation invocation,
        CancellationToken cancellationToken);
}

public sealed class RecyclarrCommandClient : IRecyclarrCommandClient
{
    private static readonly Regex InstanceNamePattern = new("^[A-Za-z0-9_]{1,64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex VersionPattern = new(
        @"(?i)(?:recyclarr\s+)?v?(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);
    private readonly IRecyclarrProcessRunner runner;
    private readonly string executablePath;
    private readonly string configurationDirectory;

    public RecyclarrCommandClient(
        IRecyclarrProcessRunner runner,
        string executablePath,
        string configurationDirectory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (!Path.IsPathFullyQualified(executablePath)
            || !Path.IsPathFullyQualified(configurationDirectory))
            throw new ArgumentException("Recyclarr paths must be absolute.");
        this.runner = runner;
        this.executablePath = executablePath;
        this.configurationDirectory = configurationDirectory;
    }

    public async Task<RecyclarrVersionResult> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(new RecyclarrProcessInvocation(
                executablePath, configurationDirectory, ["--version"], VersionTimeout), cancellationToken);
            if (result.ExitCode != 0)
                return new RecyclarrVersionResult(false, null, ProviderErrorCodes.Unknown);
            var match = VersionPattern.Match(result.Output);
            if (!match.Success || !Version.TryParse(
                    match.Groups["version"].Value.Split('-', '+')[0], out var version))
                return new RecyclarrVersionResult(false, null, ProviderErrorCodes.InvalidResponse);
            if (version.Major is not 7 and not 8)
                return new RecyclarrVersionResult(false, match.Groups["version"].Value,
                    ProviderErrorCodes.UnsupportedVersion);
            return new RecyclarrVersionResult(true, match.Groups["version"].Value, null);
        }
        catch (ProviderTransportException exception)
        {
            return new RecyclarrVersionResult(false, null, exception.Code);
        }
    }

    public async Task<RecyclarrSyncResult> SyncAsync(
        RecyclarrSyncRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var service = request.Service?.Trim().ToLowerInvariant();
        if (service is not null and not "sonarr" and not "radarr"
            || request.InstanceNames.Count > 100
            || request.InstanceNames.Any(value => !InstanceNamePattern.IsMatch(value))
            || request.InstanceNames.Distinct(StringComparer.Ordinal).Count() != request.InstanceNames.Count)
            return new RecyclarrSyncResult(false, request.Preview, null, ProviderErrorCodes.InvalidResponse);

        var version = await GetVersionAsync(cancellationToken);
        if (!version.Success)
            return new RecyclarrSyncResult(false, request.Preview, null, version.ErrorCode);

        var arguments = new List<string> { "sync" };
        if (service is not null) arguments.Add(service);
        foreach (var instanceName in request.InstanceNames)
        {
            arguments.Add("--instance");
            arguments.Add(instanceName);
        }
        if (request.Preview) arguments.Add("--preview");
        arguments.Add("--log");
        arguments.Add("info");
        try
        {
            var result = await runner.RunAsync(new RecyclarrProcessInvocation(
                executablePath, configurationDirectory, arguments, SyncTimeout), cancellationToken);
            return result.ExitCode == 0
                ? new RecyclarrSyncResult(true, request.Preview, result.ExitCode, null)
                : new RecyclarrSyncResult(false, request.Preview, result.ExitCode, ProviderErrorCodes.Unknown);
        }
        catch (ProviderTransportException exception)
        {
            return new RecyclarrSyncResult(false, request.Preview, null, exception.Code);
        }
    }
}

public sealed class RecyclarrProcessRunner : IRecyclarrProcessRunner
{
    private const int MaximumOutputCharacters = 64 * 1024;

    public async Task<RecyclarrProcessResult> RunAsync(
        RecyclarrProcessInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.ExecutablePath,
            WorkingDirectory = invocation.ConfigurationDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in invocation.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["RECYCLARR_CONFIG_DIR"] = invocation.ConfigurationDirectory;
        startInfo.Environment["RECYCLARR_DATA_DIR"] = invocation.ConfigurationDirectory;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new ProviderTransportException(ProviderErrorCodes.Unreachable);
        }
        catch (Exception exception) when (exception is not ProviderTransportException)
        {
            throw new ProviderTransportException(ProviderErrorCodes.Unreachable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(invocation.Timeout);
        var standardOutput = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var standardError = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await standardOutput;
            var error = await standardError;
            return new RecyclarrProcessResult(process.ExitCode, output + Environment.NewLine + error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new ProviderTransportException(ProviderErrorCodes.Timeout);
        }
        catch (InvalidDataException)
        {
            Kill(process);
            throw new ProviderTransportException(ProviderErrorCodes.InvalidResponse);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) return result.ToString();
            if (result.Length + read > MaximumOutputCharacters) throw new InvalidDataException();
            result.Append(buffer, 0, read);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }
}
