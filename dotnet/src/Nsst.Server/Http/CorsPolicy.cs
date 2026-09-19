using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace Nsst.Server.Http;

/// <summary>
/// The cross-origin configuration, as read from the environment.
/// </summary>
/// <remarks>
/// No configured origins means no CORS headers at all, so browsers only allow same-origin
/// requests. <c>*</c> opts in to unrestricted cross-origin access; anything else is an exact
/// origin allow list.
/// </remarks>
public sealed class CorsPolicy
{
    private readonly HashSet<string> _origins;

    public CorsPolicy(IEnumerable<string> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        _origins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in origins)
        {
            var trimmed = origin.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed == "*")
            {
                AllowAll = true;
                continue;
            }

            _origins.Add(trimmed);
        }
    }

    /// <summary>Whether every origin is accepted.</summary>
    public bool AllowAll { get; }

    /// <summary>The exact origins that are accepted, ignoring case and order.</summary>
    public IReadOnlyCollection<string> Origins => _origins;

    /// <summary>
    /// Whether no cross-origin access is configured, which is the default.
    /// </summary>
    /// <remarks>
    /// The WebSocket handshake distinguishes this case: with no CORS list at all it enforces
    /// same-origin, whereas an explicit list replaces that check with the list.
    /// </remarks>
    public bool Empty => !AllowAll && _origins.Count == 0;

    /// <summary>
    /// The host patterns accepted by the WebSocket upgrader, or null for the default
    /// same-origin-only policy.
    /// </summary>
    /// <remarks>
    /// The WebSocket handshake does its own origin check rather than going through the CORS
    /// middleware, because a browser does not preflight a WebSocket upgrade and the framework
    /// therefore never sees it. The HTTP origin list is reused, reduced to bare hosts.
    /// </remarks>
    public IReadOnlyList<string>? OriginPatterns()
    {
        if (AllowAll || _origins.Count == 0)
        {
            return null;
        }

        var patterns = new List<string>(_origins.Count);
        foreach (var origin in _origins)
        {
            var pattern = origin;
            var scheme = pattern.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                pattern = pattern[(scheme + 3)..];
            }

            var stop = pattern.IndexOfAny(['/', '?']);
            if (stop >= 0)
            {
                pattern = pattern[..stop];
            }

            if (pattern.Length > 0)
            {
                patterns.Add(pattern);
            }
        }

        return patterns;
    }

    /// <summary>
    /// Applies this policy to the framework's CORS services.
    /// </summary>
    /// <remarks>
    /// Registered as the <em>default</em> policy so that every endpoint inherits it without
    /// having to opt in, which is what "the whole API is cross-origin accessible" means.
    /// </remarks>
    public void ApplyTo(CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDefaultPolicy(policy =>
        {
            if (AllowAll)
            {
                // Cannot be combined with credentials, which is correct here: the console
                // authenticates nothing, so there is nothing for a wildcard to leak.
                policy.AllowAnyOrigin();
            }
            else
            {
                policy.WithOrigins([.. _origins]);
            }

            // HEAD is listed as well as GET because the read routes accept both; a preflight
            // that omitted it would have the browser block a header-only request.
            policy.WithMethods(HttpMethods.Get, HttpMethods.Head, HttpMethods.Options);
            policy.AllowAnyHeader();

            // 10 minutes. Preflights are pure overhead for a client polling /api/health, and
            // the value is short enough that tightening the origin list takes effect the same
            // working day rather than the next one.
            policy.SetPreflightMaxAge(TimeSpan.FromMinutes(10));
        });
    }
}

/// <summary>
/// Adds <c>Vary: Origin</c> to any response that echoes an origin back.
/// </summary>
/// <remarks>
/// This is here because the framework's CORS middleware does not do it. Measured, not assumed:
/// with an allow list configured, a cross-origin GET answers
/// <c>Access-Control-Allow-Origin: &lt;origin&gt;</c> and no <c>Vary</c> at all — on the
/// preflight as well as the simple response.
///
/// That is a caching bug waiting to happen. The body of these responses is not origin
/// dependent, but the headers are, and without <c>Vary</c> a shared cache is entitled to store
/// the response for one origin and replay it to another. The client then sees either an
/// origin that is not its own (blocked) or a cached response with no CORS headers at all
/// (also blocked) — so cross-origin access becomes intermittently broken in a way that only
/// reproduces behind a proxy.
///
/// The header is added before the pipeline runs, so it is in place no matter which handler
/// ends up writing the response, including the error paths.
/// </remarks>
public sealed class VaryOriginMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.ContainsKey(HeaderNames.Origin))
        {
            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Origin);
        }

        await _next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// Converts an unhandled exception into a 500 response and a structured log entry.
/// </summary>
/// <remarks>
/// This is the outermost middleware by design: it is the last thing between an unexpected
/// exception and a truncated response.
/// </remarks>
public sealed class RecovererMiddleware(RequestDelegate next, ILogger<RecovererMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<RecovererMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The peer went away mid-request. Nothing to report and nothing to write.
            throw;
        }
        catch (Exception error)
        {
            _logger.LogError(
                error,
                "unhandled error method={Method} path={Path} remote={Remote}",
                context.Request.Method,
                context.Request.Path.Value,
                context.Connection.RemoteIpAddress?.ToString());

            if (!context.Response.HasStarted)
            {
                await HttpResponses.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.Internal,
                    "internal server error").ConfigureAwait(false);
            }

            // Deliberately not rethrown. Once a response has started, letting the exception
            // escape hands it to Kestrel, which aborts the connection — so a client that was
            // already being sent a body can lose it. Swallowing here lets the response finish
            // cleanly, which is strictly better for the caller and loses no information, since
            // the failure has already been logged in full above.
        }
    }
}
