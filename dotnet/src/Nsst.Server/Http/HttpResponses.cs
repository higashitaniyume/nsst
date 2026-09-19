using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nsst.Core.Streaming;
using Nsst.Server.Api;

namespace Nsst.Server.Http;

/// <summary>The JSON body returned for every non-2xx API response.</summary>
public sealed record ErrorBody
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("param")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Param { get; init; }
}

/// <summary>The error codes used by the API.</summary>
public static class ErrorCodes
{
    public const string InvalidParameter = "invalid_parameter";
    public const string TooManyStreams = "too_many_streams";
    public const string ShuttingDown = "server_shutting_down";
    public const string NotFound = "not_found";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string Internal = "internal_error";
}

/// <summary>Writes the bodies the API returns.</summary>
/// <remarks>
/// Every JSON body goes through the source-generated <c>ApiJsonContext</c> and is buffered
/// before the response is touched. Buffering costs one allocation per REST response, which is
/// worth paying: it makes <c>Content-Length</c> exact, keeps the response out of chunked
/// encoding, and means a serialisation failure surfaces as a clean 500 instead of a truncated
/// body. None of this is on the streaming path, where the frame encoder writes into a reused
/// buffer and allocates nothing.
/// </remarks>
public static class HttpResponses
{
    /// <summary>Serialises <paramref name="body"/> with a source-generated contract.</summary>
    public static Task WriteJsonAsync<T>(HttpContext context, int status, T body, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return WriteRawAsync(context, status, JsonSerializer.SerializeToUtf8Bytes(body, typeInfo));
    }

    /// <summary>Renders a structured error body.</summary>
    public static Task WriteErrorAsync(HttpContext context, int status, string code, string message, string? param = null)
    {
        var body = new ErrorBody
        {
            Error = code,
            Message = message,
            Param = string.IsNullOrEmpty(param) ? null : param,
        };

        return WriteJsonAsync(context, status, body, ApiJsonContext.Default.ErrorBody);
    }

    /// <summary>Maps a <see cref="StreamManager.Acquire"/> failure onto an HTTP response.</summary>
    /// <remarks>
    /// The <c>Retry-After</c> values tell a client roughly how long to wait: a full
    /// concurrency budget frees up as other tests finish, whereas a shutting-down
    /// server is waiting on its own drain budget.
    /// </remarks>
    public static Task WriteAcquireErrorAsync(HttpContext context, StreamRejectedException error)
    {
        ArgumentNullException.ThrowIfNull(error);

        switch (error.Reason)
        {
            case StreamRejectionReason.TooManyStreams:
                context.Response.Headers.RetryAfter = "5";
                return WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.TooManyStreams, error.Message);

            case StreamRejectionReason.ServerShuttingDown:
                context.Response.Headers.RetryAfter = "10";
                return WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.ShuttingDown, error.Message);

            default:
                // The client is already gone; the status is informational only.
                return WriteErrorAsync(context, StatusCodes.Status500InternalServerError, ErrorCodes.Internal, error.Message);
        }
    }

    private static async Task WriteRawAsync(HttpContext context, int status, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";

        // A HEAD response must report the length the body would have had while sending none of
        // it. Kestrel does not do this for us: it will happily write the body of a HEAD reply,
        // so every writer has to check. The read routes accept HEAD as well as GET, so this is
        // reachable on every one of them.
        context.Response.ContentLength = body.Length;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Writes a plain-text error body.</summary>
    /// <remarks>
    /// Used where the response is not part of the JSON API and a JSON envelope would be
    /// misleading: a missing static asset, and a rejected WebSocket handshake. <c>nosniff</c>
    /// is set because the body is attacker-influenced in neither case but the content type is
    /// determined by the branch, not by the payload.
    /// </remarks>
    public static async Task WritePlainTextAsync(HttpContext context, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes(message + "\n");

        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.ContentLength = body.Length;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
