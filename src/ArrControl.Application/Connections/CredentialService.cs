using System.Text;
using ArrControl.Application.Authorization;

namespace ArrControl.Application.Connections;

public sealed class CredentialService(
    RbacAuthorizationService authorizationService,
    ICredentialProtector protector,
    ICredentialStore store)
{
    public async Task<PutCredentialResult> PutAsync(
        RbacActorContext actor,
        Guid instanceId,
        string? purpose,
        string? secret,
        CancellationToken cancellationToken)
    {
        ValidateActorAndTarget(actor, instanceId);
        var validatedPurpose = ValidatePurpose(purpose);
        ValidateSecret(secret);
        var scope = await GetAuthorizedScopeAsync(
            actor,
            instanceId,
            RbacPermissions.InstancesManage,
            cancellationToken);
        if (scope is null)
        {
            return new PutCredentialResult(PutCredentialStatus.NotFound);
        }

        if (!protector.IsConfigured)
        {
            return new PutCredentialResult(PutCredentialStatus.EncryptionUnavailable);
        }

        using var protectedCredential = protector.Protect(
            instanceId,
            validatedPurpose,
            secret!);
        var stored = await store.UpsertAsync(
            actor,
            instanceId,
            validatedPurpose,
            protectedCredential,
            cancellationToken);
        if (!stored.InstanceExists)
        {
            return new PutCredentialResult(PutCredentialStatus.NotFound);
        }

        return new PutCredentialResult(
            stored.Created ? PutCredentialStatus.Created : PutCredentialStatus.Updated,
            stored.Metadata);
    }

    public async Task<IReadOnlyList<CredentialMetadata>?> ListMetadataAsync(
        Guid userId,
        Guid sessionId,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || sessionId == Guid.Empty || instanceId == Guid.Empty)
        {
            throw new CredentialValidationException("credential_target_invalid");
        }

        var scope = await store.GetInstanceScopeAsync(instanceId, cancellationToken);
        if (!scope.Exists
            || !await authorizationService.HasInstanceGroupAsync(
                userId,
                sessionId,
                RbacPermissions.InstancesRead,
                scope.InstanceGroupId,
                cancellationToken))
        {
            return null;
        }

        return await store.ListMetadataAsync(instanceId, cancellationToken);
    }

    public async Task<DeleteCredentialStatus> DeleteAsync(
        RbacActorContext actor,
        Guid instanceId,
        string? purpose,
        CancellationToken cancellationToken)
    {
        ValidateActorAndTarget(actor, instanceId);
        var validatedPurpose = ValidatePurpose(purpose);
        var scope = await GetAuthorizedScopeAsync(
            actor,
            instanceId,
            RbacPermissions.InstancesManage,
            cancellationToken);
        if (scope is null)
        {
            return DeleteCredentialStatus.NotFound;
        }

        var deleted = await store.DeleteAsync(
            actor,
            instanceId,
            validatedPurpose,
            cancellationToken);
        return !deleted.InstanceExists
            ? DeleteCredentialStatus.NotFound
            : deleted.Deleted
                ? DeleteCredentialStatus.Deleted
                : DeleteCredentialStatus.Absent;
    }

    public async Task<SecretCredential?> ReadForProviderAsync(
        Guid instanceId,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (instanceId == Guid.Empty || !CredentialPurposes.IsKnown(purpose))
        {
            throw new CredentialValidationException("credential_target_invalid");
        }

        if (!protector.IsConfigured)
        {
            throw new CredentialEncryptionUnavailableException();
        }

        var stored = await store.FindAsync(instanceId, purpose, cancellationToken);
        return stored is null ? null : protector.Unprotect(stored);
    }

    private async Task<InstanceCredentialScope?> GetAuthorizedScopeAsync(
        RbacActorContext actor,
        Guid instanceId,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        var scope = await store.GetInstanceScopeAsync(instanceId, cancellationToken);
        return scope.Exists
            && await authorizationService.HasInstanceGroupAsync(
                actor.UserId,
                actor.SessionId,
                permissionCode,
                scope.InstanceGroupId,
                cancellationToken)
                ? scope
                : null;
    }

    private static void ValidateActorAndTarget(RbacActorContext actor, Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserId == Guid.Empty
            || actor.SessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(actor.Email)
            || instanceId == Guid.Empty)
        {
            throw new CredentialValidationException("credential_target_invalid");
        }
    }

    private static string ValidatePurpose(string? purpose)
    {
        if (!CredentialPurposes.IsKnown(purpose))
        {
            throw new CredentialValidationException("credential_purpose_invalid");
        }

        return purpose!;
    }

    private static void ValidateSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)
            || Encoding.UTF8.GetByteCount(secret) > CredentialLimits.MaximumSecretBytes)
        {
            throw new CredentialValidationException("credential_secret_invalid");
        }
    }
}
