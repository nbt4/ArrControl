using Xunit;

namespace ArrControl.AuthentikIntegrationTests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class AuthentikContainerFactAttribute : FactAttribute
{
    private const string ContainerVariable = "ARRCONTROL_RUN_AUTHENTIK_TESTS";
    private const string BrowserVariable = "ARRCONTROL_RUN_AUTHENTIK_E2E";

    public AuthentikContainerFactAttribute()
    {
        if (!IsEnabled(ContainerVariable) && !IsEnabled(BrowserVariable))
        {
            Skip = $"Set {ContainerVariable}=1 to run the real Authentik container tests.";
        }
    }

    internal static bool IsEnabled(string variable) =>
        string.Equals(
            Environment.GetEnvironmentVariable(variable),
            "1",
            StringComparison.Ordinal);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class AuthentikBrowserFactAttribute : FactAttribute
{
    private const string BrowserVariable = "ARRCONTROL_RUN_AUTHENTIK_E2E";

    public AuthentikBrowserFactAttribute()
    {
        if (!AuthentikContainerFactAttribute.IsEnabled(BrowserVariable))
        {
            Skip = $"Set {BrowserVariable}=1 to run the real browser Authorization Code + PKCE flow.";
        }
    }
}
