using RedigiAI.Api.Http;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Endpoints;

internal static class CatalogEndpoints
{
    private const int MaxSearchLength = 80;

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/v1").WithTags("Catalog").AllowAnonymous();

        catalog.MapGet("/exams", async (ICatalogQueries queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.ListExamsAsync(ct)))
            .WithName("listExams");

        catalog.MapGet("/exams/{slug}/themes", async (string slug, string? q, ICatalogQueries queries, CancellationToken ct) =>
            {
                var search = q is { Length: > MaxSearchLength } ? q[..MaxSearchLength] : q;
                var themes = await queries.ListThemesAsync(slug, search, ct) ?? throw Errors.NotFound();
                return TypedResults.Ok(themes);
            })
            .WithName("listThemes")
            .ProducesError(404);

        catalog.MapGet("/themes/{id:guid}", async (Guid id, ICatalogQueries queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.GetThemeAsync(id, ct) ?? throw Errors.ThemeNotAvailable()))
            .WithName("getTheme")
            .ProducesError(404);

        catalog.MapGet("/rubrics/{versionId:guid}", async (Guid versionId, ICatalogQueries queries, CancellationToken ct) =>
                TypedResults.Ok(await queries.GetRubricAsync(versionId, ct) ?? throw Errors.NotFound()))
            .WithName("getRubric")
            .ProducesError(404);
    }

    public static RouteHandlerBuilder ProducesError(this RouteHandlerBuilder builder, params int[] statusCodes)
    {
        foreach (var statusCode in statusCodes)
        {
            builder.Produces<ErrorResponse>(statusCode);
        }

        return builder;
    }
}
