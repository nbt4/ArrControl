using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace ArrControl.Api.Identity;

public sealed class CsrfTokenService
{
    private const int TokenLength = 32;
    private const int EncodedTokenLength = 43;

    public string Issue()
    {
        var token = RandomNumberGenerator.GetBytes(TokenLength);
        try
        {
            return EncodeBase64Url(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    public bool Validate(string? cookieToken, StringValues headerValues)
    {
        if (headerValues.Count != 1 || !TryDecode(cookieToken, out var cookieBytes))
        {
            return false;
        }

        try
        {
            if (!TryDecode(headerValues[0], out var headerBytes))
            {
                return false;
            }

            try
            {
                return CryptographicOperations.FixedTimeEquals(cookieBytes, headerBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(headerBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cookieBytes);
        }
    }

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length != EncodedTokenLength)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != TokenLength
                || !string.Equals(EncodeBase64Url(bytes), value, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(bytes);
                bytes = [];
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeBase64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class RequireCsrfTokenFilter(CsrfTokenService tokenService) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!request.Cookies.TryGetValue(LocalAuthApiConstants.CsrfCookieName, out var cookieToken)
            || !request.Headers.TryGetValue(LocalAuthApiConstants.CsrfHeaderName, out var headerToken)
            || !tokenService.Validate(cookieToken, headerToken))
        {
            return ValueTask.FromResult<object?>(AuthApiProblem.Create(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "Request verification failed.",
                "csrf_validation_failed"));
        }

        return next(context);
    }
}
