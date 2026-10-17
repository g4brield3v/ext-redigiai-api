using System.Diagnostics;
using System.Text.Json.Serialization;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Http;

/// <summary>
/// Formato único de erro da API (especificação, seção 12). <c>Details</c> só vem em <c>VALIDATION_FAILED</c>
/// (rotas de administração): cada pendência, com o campo a que se refere.
/// </summary>
public sealed record ErrorResponse(
    string Code,
    string Message,
    bool Retryable,
    string TraceId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ErrorDetail>? Details = null);

public sealed record ErrorDetail(string Path, string Message);

internal static class ErrorWriter
{
    public static string TraceIdOf(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    public static Task WriteAsync(HttpContext context, AppError error)
    {
        context.Response.StatusCode = error.HttpStatus;
        return context.Response.WriteAsJsonAsync(
            new ErrorResponse(
                error.Code,
                error.UserMessage,
                error.Retryable,
                TraceIdOf(context),
                error.Details is null ? null : [.. error.Details.Select(issue => new ErrorDetail(issue.Path, issue.Message))]),
            context.RequestAborted);
    }
}

/// <summary>
/// Abre o escopo de log com o <c>traceId</c> e converte exceções no formato de erro.
/// Exceção desconhecida vira INTERNAL_ERROR, com os detalhes só no log.
/// </summary>
internal sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = ErrorWriter.TraceIdOf(context);
        context.Response.Headers["X-Trace-Id"] = traceId;

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["traceId"] = traceId });
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await ErrorWriter.WriteAsync(context, ToAppError(ex, context));
        }
    }

    private AppError ToAppError(Exception exception, HttpContext context)
    {
        switch (exception)
        {
            case AppError known:
                logger.LogInformation("Request rejected with {Code}", known.Code);
                return known;
            case BadHttpRequestException:
                return Errors.Validation("Não foi possível ler os dados enviados.");
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return new AppError("CLIENT_CLOSED_REQUEST", 499, "Requisição cancelada.");
            default:
                logger.LogError(exception, "Unhandled exception");
                return Errors.Internal();
        }
    }
}

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        var isSwagger = context.Request.Path.StartsWithSegments("/swagger");
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        // A API só devolve JSON e PDF: nada aqui deve carregar recursos nem ser embutido.
        // A página do Swagger (só em desenvolvimento) precisa carregar os próprios scripts e estilos.
        headers.ContentSecurityPolicy = isSwagger
            ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'"
            : "default-src 'none'; frame-ancestors 'none'";
        return next(context);
    }
}
