using System.Text.Json;
using MeetUp.Api.Infrastructure.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace MeetUp.Api.Infrastructure.Middleware;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "An unhandled exception occurred: {ExceptionMessage}", exception.Message);

        var response = httpContext.Response;
        response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails();

        switch (exception)
        {
            case NotFoundException notFound:
                response.StatusCode = StatusCodes.Status404NotFound;
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Not Found",
                    Detail = notFound.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
                };
                break;

            case ConflictException conflict:
                response.StatusCode = StatusCodes.Status409Conflict;
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflict",
                    Detail = conflict.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8"
                };
                break;

            case ForbiddenException forbidden:
                response.StatusCode = StatusCodes.Status403Forbidden;
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = forbidden.Message,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3"
                };
                break;

            case ValidationException validation:
                response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = "Validation Failed",
                    Detail = "One or more validation errors occurred.",
                    Type = "https://tools.ietf.org/html/rfc4918#section-11.2",
                    Extensions = new Dictionary<string, object?> { { "errors", validation.Errors } }
                };
                break;

            default:
                response.StatusCode = StatusCodes.Status500InternalServerError;
                problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Internal Server Error",
                    Detail = "An internal server error occurred. Please try again later.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
                };
                break;
        }

        var json = JsonSerializer.Serialize(problemDetails, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await response.WriteAsync(json, cancellationToken);
        return true;
    }
}
