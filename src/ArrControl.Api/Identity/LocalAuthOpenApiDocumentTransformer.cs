using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace ArrControl.Api.Identity;

public sealed class LocalAuthOpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["cookieAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = LocalAuthApiConstants.AccessCookieName,
            Description = "Short-lived opaque access token in a Secure, HttpOnly, SameSite cookie.",
        };
        document.Components.SecuritySchemes["refreshCookie"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = LocalAuthApiConstants.RefreshCookieName,
            Description = "Rotating opaque refresh token in a Secure, HttpOnly, SameSite host cookie.",
        };
        document.SecurityRequirements ??= [];
        document.SecurityRequirements.Add(CreateSecurityRequirement("cookieAuth"));

        foreach (var path in document.Paths)
        {
            foreach (var operation in path.Value.Operations.Values)
            {
                if (path.Key is "/api/v1/system/status"
                    or "/api/v1/auth/csrf"
                    or "/api/v1/auth/login"
                    or "/api/v1/auth/logout"
                    or OidcAuthenticationApi.StatusPath
                    or OidcAuthenticationApi.LoginPath
                    or OidcAuthenticationApi.LogoutPath)
                {
                    operation.Security = [new OpenApiSecurityRequirement()];
                }
                else if (path.Key == "/api/v1/auth/refresh")
                {
                    operation.Security = [CreateSecurityRequirement("refreshCookie")];
                }
                if (path.Key is "/api/v1/auth/login"
                    or "/api/v1/auth/refresh"
                    or "/api/v1/auth/logout"
                    or OidcAuthenticationApi.LogoutPath)
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = LocalAuthApiConstants.CsrfHeaderName,
                        In = ParameterLocation.Header,
                        Required = true,
                        Description = $"Must exactly match the {LocalAuthApiConstants.CsrfCookieName} cookie.",
                        Schema = new OpenApiSchema
                        {
                            Type = "string",
                            MinLength = 43,
                            MaxLength = 43,
                        },
                    });
                }
            }
        }

        return Task.CompletedTask;
    }

    private static OpenApiSecurityRequirement CreateSecurityRequirement(string schemeName) =>
        new()
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Id = schemeName,
                    Type = ReferenceType.SecurityScheme,
                },
            }] = [],
        };
}

public sealed class LocalAuthOpenApiSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(ProblemDetails))
        {
            schema.AdditionalPropertiesAllowed = false;
            schema.Properties["code"] = new OpenApiSchema
            {
                Type = "string",
                Description = "Stable machine-readable error code.",
            };
            schema.Properties["traceId"] = new OpenApiSchema
            {
                Type = "string",
                Description = "Request correlation identifier.",
            };
            schema.Required = new HashSet<string>(StringComparer.Ordinal)
            {
                "type",
                "title",
                "status",
                "code",
                "traceId",
            };
        }
        else if (context.JsonTypeInfo.Type == typeof(LoginRequest))
        {
            schema.AdditionalPropertiesAllowed = false;
            schema.Required = new HashSet<string>(StringComparer.Ordinal)
            {
                "email",
                "password",
            };
            if (schema.Properties.TryGetValue("email", out var email))
            {
                email.Nullable = false;
                email.Format = "email";
                email.MaxLength = 320;
            }

            if (schema.Properties.TryGetValue("password", out var password))
            {
                password.Nullable = false;
                password.Format = "password";
                password.MaxLength = 1024;
                password.WriteOnly = true;
            }
        }
        else if (context.JsonTypeInfo.Type == typeof(CsrfTokenResponse)
            && schema.Properties.TryGetValue("token", out var csrfToken))
        {
            schema.AdditionalPropertiesAllowed = false;
            csrfToken.MinLength = 43;
            csrfToken.MaxLength = 43;
        }
        else if (context.JsonTypeInfo.Type == typeof(AuthSessionResponse))
        {
            schema.AdditionalPropertiesAllowed = false;
            if (schema.Properties.TryGetValue("email", out var email))
            {
                email.Format = "email";
            }

            if (schema.Properties.TryGetValue("csrfToken", out var sessionCsrfToken))
            {
                sessionCsrfToken.MinLength = 43;
                sessionCsrfToken.MaxLength = 43;
            }
        }
        else if (context.JsonTypeInfo.Type == typeof(OidcStatusResponse)
            && schema.Properties.TryGetValue("loginUrl", out var loginUrl))
        {
            schema.AdditionalPropertiesAllowed = false;
            loginUrl.MaxLength = 2048;
        }

        return Task.CompletedTask;
    }
}
