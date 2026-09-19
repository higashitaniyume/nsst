using System.Net;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Connections.Features;
using Nsst.Core.Metrics;
using Nsst.Core.Streaming;
using Nsst.Server.Http;

namespace Nsst.Server.Api;

/// <summary>
/// The REST response shapes.
/// </summary>
/// <remarks>
/// These are plain records serialised by the source-generated <c>ApiJsonContext</c>. Property
/// names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than derived from a
/// naming policy, so renaming a C# property is a compile-time-safe refactor that cannot change
/// the wire format by accident.
///
/// Property order is the wire order. The serialiser emits members in declaration order, so the
/// declared order is deliberate and matches what the console was written against.
/// </remarks>
public sealed record LimitsView
{
    [JsonPropertyName("max_duration")]
    public required long MaxDuration { get; init; }

    [JsonPropertyName("min_duration")]
    public required long MinDuration { get; init; }

    [JsonPropertyName("max_interval")]
    public required long MaxInterval { get; init; }

    [JsonPropertyName("min_interval")]
    public required long MinInterval { get; init; }

    [JsonPropertyName("max_payload_size")]
    public required int MaxPayloadSize { get; init; }

    [JsonPropertyName("max_concurrent_streams")]
    public required int MaxConcurrentStreams { get; init; }

    [JsonPropertyName("write_timeout_ms")]
    public required long WriteTimeoutMs { get; init; }
}

/// <summary>The defaults a client gets when it omits a parameter.</summary>
public sealed record DefaultsView
{
    [JsonPropertyName("duration")]
    public required long Duration { get; init; }

    [JsonPropertyName("interval")]
    public required long Interval { get; init; }

    [JsonPropertyName("payload_size")]
    public required int PayloadSize { get; init; }
}

public sealed record HealthResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("uptime_seconds")]
    public required long UptimeSeconds { get; init; }
}

public sealed record InfoResponse
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("protocols")]
    public required IReadOnlyList<string> Protocols { get; init; }

    /// <summary>
    /// A <see cref="SortedDictionary{TKey,TValue}"/> rather than a plain dictionary so the
    /// console can render the endpoint list in a stable order. Nothing on the wire requires
    /// it, but an ordering that changes between requests makes the page flicker.
    /// </summary>
    [JsonPropertyName("endpoints")]
    public required SortedDictionary<string, string> Endpoints { get; init; }

    [JsonPropertyName("uptime_seconds")]
    public required long UptimeSeconds { get; init; }

    [JsonPropertyName("active_streams")]
    public required long ActiveStreams { get; init; }

    [JsonPropertyName("limits")]
    public required LimitsView Limits { get; init; }

    [JsonPropertyName("defaults")]
    public required DefaultsView Defaults { get; init; }
}

public sealed record ConfigResponse
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("protocols")]
    public required IReadOnlyList<string> Protocols { get; init; }

    [JsonPropertyName("endpoints")]
    public required SortedDictionary<string, string> Endpoints { get; init; }

    [JsonPropertyName("limits")]
    public required LimitsView Limits { get; init; }

    [JsonPropertyName("defaults")]
    public required DefaultsView Defaults { get; init; }
}

/// <summary>The counters exposed by <c>/api/metrics</c>.</summary>
/// <remarks>
/// The counter fields are <see cref="ulong"/> because that is what the counters themselves
/// are: they only ever increase, and a signed type would invite the question of what a
/// negative frame count means. JSON has no signedness, so nothing on the wire changes.
/// </remarks>
public sealed record MetricsView
{
    [JsonPropertyName("active_streams")]
    public required long ActiveStreams { get; init; }

    [JsonPropertyName("streams_started")]
    public required ulong StreamsStarted { get; init; }

    [JsonPropertyName("streams_finished")]
    public required ulong StreamsFinished { get; init; }

    [JsonPropertyName("streams_cancelled")]
    public required ulong StreamsCancelled { get; init; }

    [JsonPropertyName("streams_errored")]
    public required ulong StreamsErrored { get; init; }

    [JsonPropertyName("streams_rejected")]
    public required ulong StreamsRejected { get; init; }

    [JsonPropertyName("frames_sent")]
    public required ulong FramesSent { get; init; }

    [JsonPropertyName("bytes_sent")]
    public required ulong BytesSent { get; init; }
}

/// <summary>This request as the server sees it.</summary>
/// <remarks>
/// Every field is either observed on the connection or read from a request header; nothing
/// is resolved against an external service, so the console can show it without the page
/// talking to anyone but this server.
/// </remarks>
public sealed record ClientResponse
{
    [JsonPropertyName("ip")]
    public required string Ip { get; init; }

    /// <summary>Names where <see cref="Ip"/> came from, so a proxy header is never
    /// mistaken for the socket peer.</summary>
    [JsonPropertyName("ip_source")]
    public required string IpSource { get; init; }

    [JsonPropertyName("proxy")]
    public required bool Proxy { get; init; }

    [JsonPropertyName("remote_addr")]
    public required string RemoteAddr { get; init; }

    [JsonPropertyName("forwarded_for")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ForwardedFor { get; init; }

    [JsonPropertyName("user_agent")]
    public required string UserAgent { get; init; }

    [JsonPropertyName("accept_language")]
    public required string AcceptLanguage { get; init; }

    [JsonPropertyName("referer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Referer { get; init; }

    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("proto")]
    public required string Proto { get; init; }

    [JsonPropertyName("tls")]
    public required bool Tls { get; init; }

    [JsonPropertyName("tls_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TlsVersion { get; init; }

    /// <summary>Unix milliseconds, matching the frame field, so the console can report how
    /// far the browser clock is from the server clock.</summary>
    [JsonPropertyName("server_time")]
    public required long ServerTime { get; init; }
}

/// <summary>The REST endpoints and the metadata they share.</summary>
public sealed class ApiSurface(
    ServerConfig config,
    StreamManager manager,
    Counters counters,
    TimeProvider timeProvider)
{
    /// <summary>The canonical protocol identifier list.</summary>
    public static readonly string[] Protocols = ["http-stream", "sse", "websocket"];

    /// <summary>Known paths and the methods they accept, for 405 rather than 404.</summary>
    /// <remarks>
    /// This is hand-maintained, and the price of forgetting to update it is a 404 where a 405
    /// belongs — a wrong answer, not a crash. <c>RoutingTests</c> asserts it against the
    /// endpoints <c>Program</c> actually maps, so the two cannot drift unnoticed.
    /// </remarks>
    internal static readonly SortedDictionary<string, string> KnownRoutes = new(StringComparer.Ordinal)
    {
        ["/api/client"] = "GET, HEAD, OPTIONS",
        ["/api/config"] = "GET, HEAD, OPTIONS",
        ["/api/health"] = "GET, HEAD, OPTIONS",
        ["/api/info"] = "GET, HEAD, OPTIONS",
        ["/api/metrics"] = "GET, HEAD, OPTIONS",
        ["/api/stream/http"] = "GET, OPTIONS",
        ["/api/stream/sse"] = "GET, OPTIONS",
        ["/api/stream/ws"] = "GET, OPTIONS",
    };

    private readonly ServerConfig _config = config;
    private readonly StreamManager _manager = manager;
    private readonly Counters _counters = counters;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    /// <summary>How long the process has been serving.</summary>
    private TimeSpan Uptime => _timeProvider.GetUtcNow() - _startedAt;

    public Task HealthAsync(HttpContext context)
    {
        var body = new HealthResponse
        {
            Status = "ok",
            Version = ServerConfig.ServiceVersion,
            UptimeSeconds = (long)Uptime.TotalSeconds,
        };

        return WriteAsync(context, body, ApiJsonContext.Default.HealthResponse);
    }

    public Task InfoAsync(HttpContext context)
    {
        var body = new InfoResponse
        {
            Name = ServerConfig.ServiceName,
            Version = ServerConfig.ServiceVersion,
            Protocols = Protocols,
            Endpoints = Endpoints(),
            UptimeSeconds = (long)Uptime.TotalSeconds,
            ActiveStreams = _counters.ActiveStreams,
            Limits = LimitsView(),
            Defaults = DefaultsView(),
        };

        return WriteAsync(context, body, ApiJsonContext.Default.InfoResponse);
    }

    public Task ConfigAsync(HttpContext context)
    {
        var body = new ConfigResponse
        {
            Name = ServerConfig.ServiceName,
            Version = ServerConfig.ServiceVersion,
            Protocols = Protocols,
            Endpoints = Endpoints(),
            Limits = LimitsView(),
            Defaults = DefaultsView(),
        };

        return WriteAsync(context, body, ApiJsonContext.Default.ConfigResponse);
    }

    public Task MetricsAsync(HttpContext context)
    {
        var snapshot = _counters.TakeSnapshot();

        var body = new MetricsView
        {
            ActiveStreams = snapshot.ActiveStreams,
            StreamsStarted = snapshot.StreamsStarted,
            StreamsFinished = snapshot.StreamsFinished,
            StreamsCancelled = snapshot.CancelledStreams,
            StreamsErrored = snapshot.ErroredStreams,
            StreamsRejected = snapshot.RejectedStreams,
            FramesSent = snapshot.FramesSent,
            BytesSent = snapshot.BytesSent,
        };

        return WriteAsync(context, body, ApiJsonContext.Default.MetricsView);
    }

    public Task ClientAsync(HttpContext context)
    {
        var (ip, source) = ClientIp.Resolve(context);
        var forwarded = ClientIp.ForwardedFor(context);

        var body = new ClientResponse
        {
            Ip = ip,
            IpSource = source,
            Proxy = !string.Equals(source, ClientIp.SourceRemoteAddr, StringComparison.Ordinal),
            RemoteAddr = ClientIp.RemoteAddr(context),
            ForwardedFor = forwarded.Count > 0 ? forwarded : null,
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            AcceptLanguage = context.Request.Headers.AcceptLanguage.ToString(),
            Referer = NullIfEmpty(context.Request.Headers.Referer.ToString()),
            Host = context.Request.Host.Value ?? string.Empty,
            Proto = NormalizeProtocol(context.Request.Protocol),
            Tls = context.Request.IsHttps,
            TlsVersion = TlsVersionName(context),
            ServerTime = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
        };

        return WriteAsync(context, body, ApiJsonContext.Default.ClientResponse);
    }

    /// <summary>
    /// Answers requests under <c>/api/</c> that no route matched. Known paths reached with
    /// the wrong method get 405 rather than a confusing 404.
    /// </summary>
    public Task UnknownAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Exactly one trailing slash is trimmed, and the distinction is observable:
        // "/api/health//" trims to "/api/health/" and is therefore not a known route (404),
        // where trimming every slash would have recognised "/api/health" and answered 405.
        var clean = path.EndsWith('/') ? path[..^1] : path;

        if (KnownRoutes.TryGetValue(clean, out var methods))
        {
            context.Response.Headers.Allow = methods;
            return HttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status405MethodNotAllowed,
                ErrorCodes.MethodNotAllowed,
                "method " + context.Request.Method + " is not allowed for " + clean);
        }

        return HttpResponses.WriteErrorAsync(
            context,
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            "no such endpoint: " + path);
    }

    private static Task WriteAsync<T>(HttpContext context, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        HttpResponses.WriteJsonAsync(context, StatusCodes.Status200OK, body, typeInfo);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// Restates the request protocol in the spelling the console documents.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core reports <c>HTTP/2</c>; the console and its type definitions say
    /// <c>HTTP/2.0</c>. Normalising here keeps the difference out of the browser rather than
    /// spreading the special case across the front end.
    /// </remarks>
    internal static string NormalizeProtocol(string protocol) =>
        protocol.StartsWith("HTTP/", StringComparison.Ordinal) && !protocol.Contains('.')
            ? protocol + ".0"
            : protocol;

    /// <remarks>
    /// The named <c>SslProtocols</c> members for TLS 1.0 and 1.1 are obsolete in .NET 8 and
    /// this project treats warnings as errors, so they are compared by their wire values.
    /// </remarks>
    private static string? TlsVersionName(HttpContext context)
    {
        const SslProtocols Tls10 = (SslProtocols)0x0300;
        const SslProtocols Tls11 = (SslProtocols)0x0301;

        if (!context.Request.IsHttps)
        {
            return null;
        }

        var feature = context.Features.Get<ITlsHandshakeFeature>();
        if (feature is null)
        {
            // Terminated TLS upstream: the connection to this server is plain HTTP.
            return null;
        }

        return feature.Protocol switch
        {
            Tls10 => "TLS 1.0",
            Tls11 => "TLS 1.1",
            SslProtocols.Tls12 => "TLS 1.2",
            SslProtocols.Tls13 => "TLS 1.3",
            _ => "TLS",
        };
    }

    private static SortedDictionary<string, string> Endpoints() => new(StringComparer.Ordinal)
    {
        ["client"] = "/api/client",
        ["config"] = "/api/config",
        ["health"] = "/api/health",
        ["http"] = "/api/stream/http",
        ["info"] = "/api/info",
        ["metrics"] = "/api/metrics",
        ["sse"] = "/api/stream/sse",
        ["websocket"] = "/api/stream/ws",
    };

    private LimitsView LimitsView()
    {
        var limits = _config.Limits;
        return new LimitsView
        {
            MaxDuration = (long)limits.MaxDuration.TotalSeconds,
            MinDuration = (long)limits.MinDuration.TotalSeconds,
            MaxInterval = (long)limits.MaxInterval.TotalMilliseconds,
            MinInterval = (long)limits.MinInterval.TotalMilliseconds,
            MaxPayloadSize = limits.MaxPayloadSize,
            MaxConcurrentStreams = limits.MaxConcurrentStreams,
            WriteTimeoutMs = (long)limits.WriteTimeout.TotalMilliseconds,
        };
    }

    private DefaultsView DefaultsView()
    {
        var limits = _config.Limits;
        return new DefaultsView
        {
            Duration = (long)limits.DefaultDuration.TotalSeconds,
            Interval = (long)limits.DefaultInterval.TotalMilliseconds,
            PayloadSize = limits.DefaultPayloadSize,
        };
    }
}
