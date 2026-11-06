using System.Text.Json.Nodes;
using Microsoft.OpenApi;
namespace MeterFlow.Api.Infrastructure;
public static class OpenApiGuide
{
    public static void Configure(OpenApiDocument document)
    {
        document.Info.Title = "MeterFlow API";
        document.Info.Version = "v1";
        document.Info.Description = "Register and log in through /auth. Paste the returned accessToken into Bearer authentication. Import hourly UTC interval-end readings, queue an analysis, poll status and download results. All examples are synthetic.";
        document.Components ??= new();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "ASP.NET Core Identity opaque access token (not a JWT)." }
        };
        foreach (var (path, item) in document.Paths)
        foreach (var (method, operation) in item.Operations ?? [])
        {
            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] }];
                operation.Responses ??= new();
                foreach (var (code, description) in new[] { ("400", "Invalid request or pagination"), ("401", "Bearer authentication required"), ("404", "Missing or not owned"), ("422", "Validation failed"), ("500", "Unexpected failure; quote requestId") })
                    operation.Responses.TryAdd(code, new OpenApiResponse { Description = description, Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Example = JsonNode.Parse("""{"title":"Invalid request","status":400,"detail":"Check the request parameters.","requestId":"example-request"}""") } } });
            }
            if (method != HttpMethod.Post) continue;
            string? example = path switch
            {
                "/auth/register" or "/auth/login" => """{"email":"synthetic-demo@example.test","password":"Synthetic-Demo-Password1!"}""",
                "/api/projects/" or "/api/projects" => """{"name":"Synthetic campus","description":"Hourly energy quality checks"}""",
                "/api/jobs" => """{"datasetId":"00000000-0000-0000-0000-000000000000","startDate":"2026-01-01","endDateExclusive":"2026-01-03"}""",
                _ => null
            };
            if (example is not null && operation.RequestBody?.Content?.TryGetValue("application/json", out var body) == true)
                body.Example = JsonNode.Parse(example);
            if (path == "/api/jobs")
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter { Name = "Idempotency-Key", In = ParameterLocation.Header, Required = true,
                    Description = "Same key and payload reuse the existing job; changed payload returns 409. Scoped to the authenticated user.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String }, Example = JsonValue.Create("synthetic-analysis-1") });
                operation.Responses ??= new();
                operation.Responses.Remove("200");
                operation.Responses["202"] = new OpenApiResponse { Description = "Accepted; Location and statusUrl identify the persisted job" };
                operation.Responses["409"] = new OpenApiResponse { Description = "Idempotency key reused with different settings" };
            }
            else if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                operation.Responses ??= new(); operation.Responses.Remove("200");
                operation.Responses["201"] = new OpenApiResponse { Description = "Created; Location identifies the resource" };
            }
            if (path.EndsWith("/datasets", StringComparison.Ordinal) && operation.RequestBody?.Content?.TryGetValue("text/csv", out var csv) == true)
            {
                csv.Example = JsonValue.Create("building_id,timestamp,consumption_kwh\nA,2026-01-01T01:00:00Z,0\nA,2026-01-01T02:00:00Z,2.5\n");
                operation.Description = "Raw UTF-8 CSV body. Supply a plain filename query parameter. Exactly three columns, up to 5 MiB / 50000 rows / 100 buildings. Whole-file rejection with row errors on 422. See repository samples for a complete day.";
                operation.Responses!["413"] = new OpenApiResponse { Description = "File exceeds 5 MiB" };
                operation.Responses["415"] = new OpenApiResponse { Description = "Content-Type must be text/csv" };
            }
        }
    }
}
