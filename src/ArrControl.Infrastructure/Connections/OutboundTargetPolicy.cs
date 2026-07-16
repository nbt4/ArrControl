using System.Net;
using System.Net.Sockets;
using ArrControl.Application.Connections;

namespace ArrControl.Infrastructure.Connections;

public interface IHostAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken);
}

public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class OutboundTargetPolicy(IHostAddressResolver resolver) : IOutboundTargetPolicy
{
    public async Task<ResolvedOutboundTarget> ResolveAsync(
        Uri uri,
        bool allowPrivateNetworkAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Port <= 0)
        {
            throw new OutboundTargetRejectedException("outbound_target_invalid");
        }

        IReadOnlyList<IPAddress> resolved;
        if (IPAddress.TryParse(uri.IdnHost, out var literal))
        {
            resolved = [literal];
        }
        else
        {
            try
            {
                resolved = await resolver.ResolveAsync(uri.IdnHost, cancellationToken);
            }
            catch (Exception exception) when (
                exception is SocketException or ArgumentException)
            {
                throw new OutboundTargetRejectedException("outbound_dns_failed");
            }
        }

        var addresses = resolved
            .Select(Normalize)
            .Distinct()
            .OrderBy(address => address.AddressFamily)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (addresses.Length == 0)
        {
            throw new OutboundTargetRejectedException("outbound_dns_failed");
        }

        foreach (var address in addresses)
        {
            var classification = Classify(address);
            if (classification == AddressClassification.Forbidden
                || classification == AddressClassification.Private
                    && !allowPrivateNetworkAccess)
            {
                throw new OutboundTargetRejectedException("outbound_address_blocked");
            }
        }

        return new ResolvedOutboundTarget(uri, addresses);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static AddressClassification Classify(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast)
        {
            return AddressClassification.Forbidden;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if (IsWellKnownNat64(bytes))
            {
                return Classify(new IPAddress(bytes[^4..])) == AddressClassification.Public
                    ? AddressClassification.Public
                    : AddressClassification.Forbidden;
            }

            if (address.IsIPv6SiteLocal)
            {
                return AddressClassification.Private;
            }

            if (bytes[0] == 0x20
                && bytes[1] == 0x01
                && bytes[2] == 0x0d
                && bytes[3] == 0xb8)
            {
                return AddressClassification.Forbidden;
            }

            return (bytes[0] & 0xfe) == 0xfc
                ? AddressClassification.Private
                : AddressClassification.Public;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return AddressClassification.Forbidden;
        }

        var octets = address.GetAddressBytes();
        if (octets[0] == 10
            || octets[0] == 172 && octets[1] is >= 16 and <= 31
            || octets[0] == 192 && octets[1] == 168)
        {
            return AddressClassification.Private;
        }

        if (octets[0] == 0
            || octets[0] == 100 && (octets[1] & 0xc0) == 64
            || octets[0] == 127
            || octets[0] == 169 && octets[1] == 254
            || octets[0] == 192 && octets[1] == 0 && octets[2] is 0 or 2
            || octets[0] == 192 && octets[1] == 88 && octets[2] == 99
            || octets[0] == 198 && octets[1] is 18 or 19
            || octets[0] == 198 && octets[1] == 51 && octets[2] == 100
            || octets[0] == 203 && octets[1] == 0 && octets[2] == 113
            || octets[0] >= 224)
        {
            return AddressClassification.Forbidden;
        }

        return AddressClassification.Public;
    }

    private static bool IsWellKnownNat64(byte[] bytes) =>
        bytes.Length == 16
        && bytes[0] == 0x00
        && bytes[1] == 0x64
        && bytes[2] == 0xff
        && bytes[3] == 0x9b
        && bytes[4..12].All(value => value == 0);

    private enum AddressClassification
    {
        Public,
        Private,
        Forbidden,
    }
}
