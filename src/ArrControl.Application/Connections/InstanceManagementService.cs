using ArrControl.Application.Authorization;
using ArrControl.Application.Providers;

namespace ArrControl.Application.Connections;

public sealed class InstanceManagementService(
    RbacAuthorizationService authorizationService,
    IInstanceManagementStore store,
    IOutboundTargetPolicy outboundTargetPolicy,
    IConnectionProbeTransport probeTransport,
    IEnumerable<IProviderConnectionAdapter> providerAdapters,
    CredentialService credentialService,
    TimeProvider timeProvider)
{
    public async Task<InstanceDetails?> GetAsync(
        Guid userId,
        Guid sessionId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        ValidateIdentityAndInstance(userId, sessionId, instanceId);
        var scope = await store.GetScopeAsync(instanceId, cancellationToken);
        return scope.Exists
            && await authorizationService.HasInstanceGroupAsync(
                userId,
                sessionId,
                RbacPermissions.InstancesRead,
                scope.InstanceGroupId,
                cancellationToken)
                    ? await store.GetAsync(instanceId, cancellationToken)
                    : null;
    }

    public async Task<InstanceWriteResult> CreateAsync(
        RbacActorContext actor,
        Guid instanceId,
        string? name,
        string? kind,
        string? baseUrl,
        bool enabled,
        Guid? instanceGroupId,
        bool tlsVerificationEnabled,
        bool allowPrivateNetworkAccess,
        CancellationToken cancellationToken)
    {
        ValidateActorAndInstance(actor, instanceId);
        if (!await CanManageScopeAsync(actor, instanceGroupId, cancellationToken))
        {
            return new InstanceWriteResult(InstanceWriteStatus.Forbidden);
        }

        if (instanceGroupId is Guid groupId
            && !await store.InstanceGroupExistsAsync(groupId, cancellationToken))
        {
            return new InstanceWriteResult(InstanceWriteStatus.GroupNotFound);
        }

        var input = await ValidateInputAsync(
            name,
            kind,
            baseUrl,
            enabled,
            instanceGroupId,
            tlsVerificationEnabled,
            allowPrivateNetworkAccess,
            cancellationToken);
        return await store.CreateAsync(actor, instanceId, input, cancellationToken);
    }

    public async Task<InstanceWriteResult> UpdateAsync(
        RbacActorContext actor,
        Guid instanceId,
        string? name,
        string? kind,
        string? baseUrl,
        bool enabled,
        Guid? instanceGroupId,
        bool tlsVerificationEnabled,
        bool allowPrivateNetworkAccess,
        CancellationToken cancellationToken)
    {
        ValidateActorAndInstance(actor, instanceId);
        var existingScope = await store.GetScopeAsync(instanceId, cancellationToken);
        if (!existingScope.Exists)
        {
            return new InstanceWriteResult(InstanceWriteStatus.NotFound);
        }

        if (!await CanManageScopeAsync(actor, existingScope.InstanceGroupId, cancellationToken)
            || !await CanManageScopeAsync(actor, instanceGroupId, cancellationToken))
        {
            return new InstanceWriteResult(InstanceWriteStatus.Forbidden);
        }

        if (instanceGroupId is Guid groupId
            && !await store.InstanceGroupExistsAsync(groupId, cancellationToken))
        {
            return new InstanceWriteResult(InstanceWriteStatus.GroupNotFound);
        }

        var input = await ValidateInputAsync(
            name,
            kind,
            baseUrl,
            enabled,
            instanceGroupId,
            tlsVerificationEnabled,
            allowPrivateNetworkAccess,
            cancellationToken);
        return await store.UpdateAsync(actor, instanceId, input, cancellationToken);
    }

    public async Task<InstanceDeleteStatus> DeleteAsync(
        RbacActorContext actor,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        ValidateActorAndInstance(actor, instanceId);
        var scope = await store.GetScopeAsync(instanceId, cancellationToken);
        if (!scope.Exists)
        {
            return InstanceDeleteStatus.NotFound;
        }

        if (!await CanManageScopeAsync(actor, scope.InstanceGroupId, cancellationToken))
        {
            return InstanceDeleteStatus.Forbidden;
        }

        return await store.DeleteAsync(actor, instanceId, cancellationToken);
    }

    public async Task<InstanceProbeResult> ProbeAsync(
        RbacActorContext actor,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        ValidateActorAndInstance(actor, instanceId);
        var scope = await store.GetScopeAsync(instanceId, cancellationToken);
        if (!scope.Exists)
        {
            return new InstanceProbeResult(InstanceProbeStatus.NotFound);
        }

        if (!await CanManageScopeAsync(actor, scope.InstanceGroupId, cancellationToken))
        {
            return new InstanceProbeResult(InstanceProbeStatus.Forbidden);
        }

        var instance = await store.GetAsync(instanceId, cancellationToken);
        if (instance is null)
        {
            return new InstanceProbeResult(InstanceProbeStatus.NotFound);
        }

        if (!Uri.TryCreate(instance.BaseUrl, UriKind.Absolute, out var uri))
        {
            throw new OutboundTargetRejectedException("outbound_target_invalid");
        }

        var adapter = providerAdapters.SingleOrDefault(value => value.Kind == instance.Kind);
        ConnectionProbeObservation observation;
        if (adapter is null)
        {
            var target = await outboundTargetPolicy.ResolveAsync(
                uri,
                instance.AllowPrivateNetworkAccess,
                cancellationToken);
            observation = await probeTransport.ProbeAsync(
                target,
                instance.TlsVerificationEnabled,
                cancellationToken);
        }
        else
        {
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            var credentialFailure = false;
            foreach (var purpose in adapter.RequiredCredentialPurposes.Distinct(StringComparer.Ordinal))
            {
                if (!CredentialPurposes.IsKnown(purpose))
                    throw new InvalidOperationException("The provider credential contract is invalid.");
                try
                {
                    var credential = await credentialService.ReadForProviderAsync(
                        instanceId, purpose, cancellationToken);
                    if (credential is null) credentialFailure = true;
                    else credentials[purpose] = credential.Value;
                }
                catch (Exception exception) when (
                    exception is CredentialEncryptionUnavailableException
                        or CredentialDecryptionException)
                {
                    credentialFailure = true;
                }
            }

            if (credentialFailure)
            {
                var observedAt = timeProvider.GetUtcNow();
                observation = new ConnectionProbeObservation(
                    false,
                    ProviderErrorCodes.CredentialMissing,
                    null,
                    observedAt,
                    [new ProviderCapabilityObservation(ProviderCapabilities.Probe, true, observedAt)]);
            }
            else
            {
                observation = await adapter.ProbeAsync(
                    new ProviderConnection(
                        instanceId,
                        uri,
                        instance.TlsVerificationEnabled,
                        instance.AllowPrivateNetworkAccess,
                        credentials),
                    cancellationToken);
            }
        }

        await store.SaveProbeAsync(actor, instanceId, observation, cancellationToken);
        return new InstanceProbeResult(InstanceProbeStatus.Completed, observation);
    }

    public Task<IReadOnlyList<InstanceGroupDetails>> ListGroupsAsync(
        CancellationToken cancellationToken) => store.ListGroupsAsync(cancellationToken);

    public async Task<InstanceGroupWriteResult> UpsertGroupAsync(
        RbacActorContext actor,
        Guid instanceGroupId,
        string? name,
        CancellationToken cancellationToken)
    {
        ValidateActorAndInstance(actor, instanceGroupId);
        if (!await authorizationService.HasGlobalAsync(
                actor.UserId,
                actor.SessionId,
                RbacPermissions.InstancesManage,
                cancellationToken))
        {
            return new InstanceGroupWriteResult(InstanceGroupWriteStatus.NotFound);
        }

        return await store.UpsertGroupAsync(
            actor,
            instanceGroupId,
            ValidateName(name, "instance_group_name_invalid"),
            cancellationToken);
    }

    public async Task<InstanceGroupDeleteStatus> DeleteGroupAsync(
        RbacActorContext actor,
        Guid instanceGroupId,
        CancellationToken cancellationToken)
    {
        ValidateActorAndInstance(actor, instanceGroupId);
        if (!await authorizationService.HasGlobalAsync(
                actor.UserId,
                actor.SessionId,
                RbacPermissions.InstancesManage,
                cancellationToken))
        {
            return InstanceGroupDeleteStatus.NotFound;
        }

        return await store.DeleteGroupAsync(actor, instanceGroupId, cancellationToken);
    }

    private async Task<ValidatedInstanceInput> ValidateInputAsync(
        string? name,
        string? kind,
        string? baseUrl,
        bool enabled,
        Guid? instanceGroupId,
        bool tlsVerificationEnabled,
        bool allowPrivateNetworkAccess,
        CancellationToken cancellationToken)
    {
        var validatedName = ValidateName(name, "instance_name_invalid");
        var validatedKind = kind?.Trim().ToLowerInvariant();
        if (!InstanceKinds.IsKnown(validatedKind))
        {
            throw new InstanceValidationException("instance_kind_invalid");
        }

        if (string.IsNullOrWhiteSpace(baseUrl)
            || baseUrl.Length > InstanceLimits.MaximumBaseUrlLength
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port <= 0)
        {
            throw new InstanceValidationException("instance_base_url_invalid");
        }

        var normalizedUri = new UriBuilder(uri)
        {
            Host = uri.IdnHost,
            Path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/",
        }.Uri;
        await outboundTargetPolicy.ResolveAsync(
            normalizedUri,
            allowPrivateNetworkAccess,
            cancellationToken);
        return new ValidatedInstanceInput(
            validatedName,
            validatedKind!,
            normalizedUri,
            enabled,
            instanceGroupId,
            tlsVerificationEnabled,
            allowPrivateNetworkAccess);
    }

    private Task<bool> CanManageScopeAsync(
        RbacActorContext actor,
        Guid? instanceGroupId,
        CancellationToken cancellationToken) =>
        authorizationService.HasInstanceGroupAsync(
            actor.UserId,
            actor.SessionId,
            RbacPermissions.InstancesManage,
            instanceGroupId,
            cancellationToken);

    private static string ValidateName(string? name, string code)
    {
        var value = name?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > InstanceLimits.MaximumNameLength
            || value.Any(char.IsControl))
        {
            throw new InstanceValidationException(code);
        }

        return value;
    }

    private static void ValidateIdentityAndInstance(Guid userId, Guid sessionId, Guid instanceId)
    {
        if (userId == Guid.Empty || sessionId == Guid.Empty || instanceId == Guid.Empty)
        {
            throw new InstanceValidationException("instance_target_invalid");
        }
    }

    private static void ValidateActorAndInstance(RbacActorContext actor, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.Email))
        {
            throw new InstanceValidationException("instance_target_invalid");
        }

        ValidateIdentityAndInstance(actor.UserId, actor.SessionId, instanceId);
    }
}
