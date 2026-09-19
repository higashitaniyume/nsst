using Nsst.Server.Api;

namespace Nsst.Server.Http;

/// <summary>
/// Rejects a trailing slash on the API surface before the endpoint behind it can run.
/// </summary>
/// <remarks>
/// This is a deliberate departure from ASP.NET's default routing, and the reason is not
/// cosmetic. ASP.NET treats <c>/api/stream/http</c> and <c>/api/stream/http/</c> as the same
/// route, so without this guard a client that appends a slash — a path-joining bug, a
/// copy-paste, a proxy rule — would <em>start a real stream</em>. That stream holds a
/// concurrency slot for its whole duration, and at the configured limit it is enough for one
/// mistyped URL to deny service to everyone else.
///
/// So a trailing slash is answered as a request for a different resource, which is what it is.
/// Nothing is rewritten or redirected.
///
/// Routing has already matched by the time this runs — see the pipeline order in
/// <c>Program</c> for why it sits where it does. That is harmless: matching only selects an
/// endpoint, and the endpoint does not run until the terminal middleware. Replacing the
/// response here is therefore still early enough to stop a stream from starting.
/// </remarks>
internal sealed class ApiPathMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        if (EndsWithSlashUnderApi(path))
        {
            await context.RequestServices
                .GetRequiredService<ApiSurface>()
                .UnknownAsync(context)
                .ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the path is under <c>/api/</c> and carries a trailing slash.
    /// </summary>
    /// <remarks>
    /// The root (<c>path.Length > 1</c>) is excluded so that <c>/</c> keeps serving the
    /// console. The prefix requires the slash after <c>api</c>, so <c>/apifoo</c> is not
    /// affected.
    /// </remarks>
    private static bool EndsWithSlashUnderApi(string path) =>
        path.Length > 1
        && path[^1] == '/'
        && path.StartsWith("/api/", StringComparison.Ordinal);
}
