using ArrControl.Domain;
using Xunit;

namespace ArrControl.UnitTests;

public sealed class ServiceInstanceTests
{
    [Fact] public void Instance_keeps_declared_kind() => Assert.Equal(ServiceKind.Sonarr, new ServiceInstance(Guid.NewGuid(), "TV", ServiceKind.Sonarr, new Uri("https://sonarr.invalid"), true, InstanceState.Unknown).Kind);
}
