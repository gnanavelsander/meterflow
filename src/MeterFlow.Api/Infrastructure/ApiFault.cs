using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
namespace MeterFlow.Api.Infrastructure;
public sealed class ApiFault(int status, string title, string detail, object? errors = null) : Exception(detail)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public object? Errors { get; } = errors;
    public static ApiFault Missing() => new(404, "Resource not found", "The resource does not exist or is not owned by this user.");
}
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception error, CancellationToken ct)
    {
        var fault = error as ApiFault;
        var status = fault?.Status ?? (error is BadHttpRequestException bad ? bad.StatusCode : 500);
        context.Response.StatusCode = status;
        if (status == 500) logger.LogError("Request failed with {ExceptionType}", error.GetType().Name);
        var detail = new ProblemDetails
        {
            Status = status,
            Title = fault?.Title ?? (status == 500 ? "Unexpected server error" : "Invalid request"),
            Detail = fault?.Message ?? (status == 500 ? "Retry later and quote the request ID if the problem persists." : "Check the request body and parameters.")
        };
        if (fault?.Errors is not null) detail.Extensions["errors"] = fault.Errors;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = detail });
    }
}
public static class Paging
{
    public static (int Page, int Size) Validate(int? page, int? pageSize)
    {
        var p = page ?? 1; var s = pageSize ?? 20;
        if (p < 1 || p > 10000 || s < 1 || s > 100)
            throw new ApiFault(400, "Invalid pagination", "page must be 1..10000 and pageSize must be 1..100.");
        return (p, s);
    }
}
