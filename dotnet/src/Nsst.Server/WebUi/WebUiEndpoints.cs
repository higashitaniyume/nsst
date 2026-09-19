using Microsoft.AspNetCore.StaticFiles;
using Nsst.Server.Http;

namespace Nsst.Server.WebUi;

/// <summary>
/// Serves the embedded single-page console, falling back to <c>index.html</c> so the
/// application answers unknown paths.
/// </summary>
public static class WebUiEndpoints
{
    /// <summary>
    /// Registers the console as the terminal route.
    /// </summary>
    /// <remarks>
    /// A fallback endpoint is used rather than plain middleware so that every explicitly
    /// mapped API route wins by construction — an unknown path under <c>/api/</c> is a 404
    /// from the API surface, never the SPA's index document.
    /// </remarks>
    public static void MapWebUi(this IEndpointRouteBuilder routes, WebUiAssets assets, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(logger);

        // A missing mapping is not fatal — the file is still served, as
        // application/octet-stream — but it would make a browser download an asset instead of
        // rendering it, which is the kind of bug that only shows up on a clean deploy. The
        // check runs once, at startup, where somebody will actually see it.
        foreach (var name in assets.Names)
        {
            if (!ContentTypes.TryGetContentType(name, out _))
            {
                logger.LogWarning(
                    "webui: no MIME mapping for {Asset}; it will be served as application/octet-stream",
                    name);
            }
        }

        // The pattern is spelled out because MapFallback's default pattern carries a
        // :nonfile constraint. Under that constraint a request for a missing asset such as
        // /app.css never reaches this handler at all: routing returns a bare framework 404
        // with no body, instead of the plain-text body this handler writes.
        routes.MapFallback("/{**path}", (HttpContext context) => ServeAsync(context, assets));
    }

    internal static Task ServeAsync(HttpContext context, WebUiAssets assets)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            return HttpResponses.WritePlainTextAsync(context, StatusCodes.Status405MethodNotAllowed, "method not allowed");
        }

        var name = Normalize(context.Request.Path.Value);
        if (name.Length == 0)
        {
            return ServeIndexAsync(context, assets);
        }

        if (assets.Contains(name))
        {
            if (name.StartsWith("assets/", StringComparison.Ordinal))
            {
                // Vite fingerprints asset file names, so they are immutable.
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            }
            else if (name.EndsWith(".html", StringComparison.Ordinal))
            {
                context.Response.Headers.CacheControl = "no-cache";
            }

            return ServeFileAsync(context, assets, name);
        }

        // An unknown path without a file extension is handed to the SPA. Anything that looks
        // like a static asset really is missing.
        if (Path.GetExtension(name).Length > 0)
        {
            return HttpResponses.WritePlainTextAsync(context, StatusCodes.Status404NotFound, "404 page not found");
        }

        return ServeIndexAsync(context, assets);
    }

    private static Task ServeIndexAsync(HttpContext context, WebUiAssets assets)
    {
        context.Response.Headers.CacheControl = "no-cache";
        return ServeFileAsync(context, assets, WebUiAssets.IndexPath);
    }

    /// <summary>The framework's own extension-to-MIME table.</summary>
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Resolves the content type for an embedded asset.
    /// </summary>
    /// <remarks>
    /// The framework's table does not carry a charset, so one is appended to text types. That
    /// matters here rather than being ceremony: the console and its bundle contain non-ASCII
    /// text, and a browser left to guess the encoding of <c>text/html</c> may guess wrong.
    /// </remarks>
    internal static string ContentTypeFor(string name)
    {
        if (!ContentTypes.TryGetContentType(name, out var contentType))
        {
            return "application/octet-stream";
        }

        return contentType.StartsWith("text/", StringComparison.Ordinal)
            || string.Equals(contentType, "application/json", StringComparison.Ordinal)
            || string.Equals(contentType, "application/javascript", StringComparison.Ordinal)
            ? contentType + "; charset=utf-8"
            : contentType;
    }

    private static async Task ServeFileAsync(HttpContext context, WebUiAssets assets, string name)
    {
        context.Response.ContentType = ContentTypeFor(name);

        await using var stream = assets.Open(name);
        context.Response.ContentLength = stream.Length;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Reduces a request path to a relative asset path, rejecting traversal.
    /// </summary>
    /// <remarks>
    /// The path is collapsed segment by segment, so <c>/a/../b</c> resolves to <c>b</c> and
    /// anything that climbs above the root is clamped rather than escaping the embedded file
    /// set.
    /// </remarks>
    internal static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                default:
                    parts.Add(segment);
                    continue;
            }
        }

        return string.Join('/', parts);
    }
}
