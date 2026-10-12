using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Api.Controllers;

public sealed record HealthResponse(string Status);

/// <summary>Sondas do Railway.</summary>
[ApiController]
[AllowAnonymous]
[DisableRateLimiting]
[Tags("Health")]
[Produces("application/json")]
public sealed class HealthController(IDatabaseAdmin database) : ControllerBase
{
    /// <summary>O processo está de pé.</summary>
    [HttpGet("/health", Name = "health")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new HealthResponse("ok"));

    /// <summary>O banco está acessível.</summary>
    [HttpGet("/ready", Name = "ready")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Ready(CancellationToken ct) =>
        await database.CanConnectAsync(ct)
            ? Ok(new HealthResponse("ready"))
            : StatusCode(StatusCodes.Status503ServiceUnavailable, new HealthResponse("unavailable"));
}
