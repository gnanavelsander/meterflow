using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier);
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Logging.AddJsonConsole();
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Request-ID"] = context.TraceIdentifier;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = context.TraceIdentifier });
    await next(context);
    app.Logger.LogInformation("HTTP {Method} {Path} returned {StatusCode}",
        context.Request.Method, context.Request.Path, context.Response.StatusCode);
});
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health/live");
app.Run();
public partial class Program;
