using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TransactionImport.Api.Http;

/// <summary>
/// Registers <c>X-Provider-Id</c> as an OpenAPI apiKey scheme so Swagger UI shows a top-level
/// Authorize button (set once), instead of repeating the header on every operation.
/// </summary>
internal sealed class ProviderIdSecurityDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeId = "ProviderId";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ImportEndpoints.ProviderHeader,
            Description =
                "Provider scope (like a tenant id until real auth exists). " +
                "Click Authorize, enter e.g. local-test-1, then all import calls send X-Provider-Id."
        };

        if (document.Paths is null)
        {
            return Task.CompletedTask;
        }

        foreach (var (pathKey, pathItem) in document.Paths)
        {
            if (!pathKey.StartsWith("/v1/imports", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pathItem.Operations is null)
            {
                continue;
            }

            foreach (var operation in pathItem.Operations.Values)
            {
                // Prefer Authorize button over a duplicated per-operation header parameter.
                if (operation.Parameters is { Count: > 0 })
                {
                    operation.Parameters = operation.Parameters
                        .Where(p => !(p is OpenApiParameter op
                                      && op.In == ParameterLocation.Header
                                      && string.Equals(op.Name, ImportEndpoints.ProviderHeader, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                operation.Security ??= [];
                var already = operation.Security.Any(req =>
                    req.Keys.Any(k => string.Equals(k.Reference?.Id, SchemeId, StringComparison.Ordinal)));
                if (!already)
                {
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SchemeId, document)] = []
                    });
                }
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Ensures Swagger UI shows Idempotency-Key and a real file picker for POST /v1/imports.
/// </summary>
internal sealed class CreateImportOpenApiTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath ?? string.Empty;
        var method = context.Description.HttpMethod ?? string.Empty;
        var isCreate = (path.Equals("v1/imports", StringComparison.OrdinalIgnoreCase)
                        && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                       || string.Equals(operation.OperationId, "CreateImport", StringComparison.Ordinal);
        if (!isCreate)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        EnsureHeader(operation, ImportEndpoints.IdempotencyHeader, required: true,
            "Client-generated idempotency key. Replays with the same key return the same import.");

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string> { "file" },
                        Properties = new Dictionary<string, IOpenApiSchema>
                        {
                            ["file"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "binary",
                                Description = "NDJSON transaction file (field name must be 'file')."
                            }
                        }
                    }
                }
            }
        };

        return Task.CompletedTask;
    }

    private static void EnsureHeader(OpenApiOperation operation, string name, bool required, string description)
    {
        var existing = operation.Parameters?
            .OfType<OpenApiParameter>()
            .FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                && p.In == ParameterLocation.Header);

        if (existing is not null)
        {
            existing.Required = required;
            existing.Description ??= description;
            return;
        }

        operation.Parameters!.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = required,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}

internal sealed class ImportIdPathOpenApiTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var path = context.Description.RelativePath ?? string.Empty;
        if (!path.StartsWith("v1/imports", StringComparison.OrdinalIgnoreCase)
            || operation.Parameters is null)
        {
            return Task.CompletedTask;
        }

        // Responses serialize Guids as 32-hex (N); Swagger's default uuid format rejects that.
        foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
        {
            if (parameter.In == ParameterLocation.Path
                && string.Equals(parameter.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                parameter.Description =
                    "Import id (32-hex from create response, or dashed UUID). Example: 01a1adb24ee471fa97d355525eb476ea";
                parameter.Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Pattern = @"^[0-9a-fA-F]{32}$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
                };
            }
        }

        return Task.CompletedTask;
    }
}
