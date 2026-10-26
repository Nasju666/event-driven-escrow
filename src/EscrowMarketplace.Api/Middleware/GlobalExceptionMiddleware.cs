using System.Net;
using System.Text.Json;
using EscrowMarketplace.Domain.Exceptions;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EscrowMarketplace.Api.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var response = context.Response;
        response.ContentType = "application/problem+json";

        var problemDetails = new ProblemDetails
        {
            Instance = context.Request.Path
        };

        switch (exception)
        {
            // 1. PostgreSQL GiST Exclusion Constraint Violation (SQLSTATE 23P01)
            case DbUpdateException dbEx when dbEx.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation }:
                _logger.LogWarning(exception, "Database-enforced GiST exclusion constraint violation: Overlapping booking interval detected.");
                response.StatusCode = (int)HttpStatusCode.Conflict;
                problemDetails.Status = (int)HttpStatusCode.Conflict;
                problemDetails.Title = "Temporal Conflict: Double-Booking Prevented";
                problemDetails.Detail = "The requested booking window overlaps with an existing confirmed reservation for this resource. The database-level GiST exclusion constraint (23P01) prohibited concurrent insertion.";
                problemDetails.Type = "https://marketplace.local/errors/temporal-overlap-conflict";
                break;

            // 2. Application-level temporal double booking
            case DoubleBookingExclusionException dbEx:
                _logger.LogWarning(exception, "Domain temporal reservation overlap: {Message}", dbEx.Message);
                response.StatusCode = (int)HttpStatusCode.Conflict;
                problemDetails.Status = (int)HttpStatusCode.Conflict;
                problemDetails.Title = "Booking Interval Unavailable";
                problemDetails.Detail = dbEx.Message;
                problemDetails.Type = "https://marketplace.local/errors/slot-already-reserved";
                break;

            // 3. Optimistic Concurrency Conflict (PostgreSQL xmin token mismatch)
            case DbUpdateConcurrencyException concEx:
                var entityList = string.Join(", ", concEx.Entries.Select(e => $"{e.Entity.GetType().Name} (State: {e.State})"));
                _logger.LogWarning("Optimistic concurrency conflict occurred on entities: {Entities}", entityList);
                response.StatusCode = (int)HttpStatusCode.Conflict;
                problemDetails.Status = (int)HttpStatusCode.Conflict;
                problemDetails.Title = "Concurrency Conflict";
                problemDetails.Detail = $"The record was modified by another concurrent operation. Conflicted entities: {entityList}";
                problemDetails.Type = "https://marketplace.local/errors/concurrency-conflict";
                break;

            // 4. FluentValidation Validation Failures
            case ValidationException valEx:
                _logger.LogWarning("Request validation failed: {ValidationErrors}", valEx.Errors);
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Title = "Validation Failed";
                problemDetails.Detail = "One or more validation errors occurred.";
                problemDetails.Type = "https://marketplace.local/errors/validation-failed";
                problemDetails.Extensions["errors"] = valEx.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                break;

            // 5. Entity Not Found
            case NotFoundException nfEx:
                _logger.LogWarning(exception, "Resource not found: {Message}", nfEx.Message);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                problemDetails.Status = (int)HttpStatusCode.NotFound;
                problemDetails.Title = "Resource Not Found";
                problemDetails.Detail = nfEx.Message;
                problemDetails.Type = "https://marketplace.local/errors/not-found";
                break;

            // 6. Domain Invariant Violation
            case DomainException domEx:
                _logger.LogWarning(exception, "Domain invariant violated: {Message}", domEx.Message);
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                problemDetails.Status = (int)HttpStatusCode.BadRequest;
                problemDetails.Title = "Business Invariant Violation";
                problemDetails.Detail = domEx.Message;
                problemDetails.Type = "https://marketplace.local/errors/domain-invariant-error";
                break;

            // 7. Unhandled Server Exceptions
            default:
                _logger.LogError(exception, "An unhandled exception occurred during request execution.");
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                problemDetails.Status = (int)HttpStatusCode.InternalServerError;
                problemDetails.Title = "Internal Server Error";
                problemDetails.Detail = "An unexpected error occurred on the server.";
                problemDetails.Type = "https://marketplace.local/errors/internal-server-error";
                break;
        }

        var json = JsonSerializer.Serialize(problemDetails);
        await response.WriteAsync(json);
    }
}
