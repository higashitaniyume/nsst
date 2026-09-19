using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nsst.Core.Metrics;
using Nsst.Core.Protocol;
using Nsst.Core.Streaming;
using Nsst.Server;
using Nsst.Server.Api;
using Nsst.Server.Http;
using Nsst.Server.Streaming;
using Nsst.Server.WebUi;

// ---------------------------------------------------------------------------
// Network Stream Stability Tester
//
// One process serving the web console, the REST API and the three streaming
// protocols over a single frame model. This is the only implementation: the Go
// server that used to live in cmd/, internal/ and pkg/ has been deleted.
// ---------------------------------------------------------------------------

ServerConfig config;
try
{
    config = ServerConfigLoader.Load();
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(config.LogLevel);
if (config.LogFormat == "json")
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.UseUtcTimestamp = true;
    });
}
else
{
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        options.UseUtcTimestamp = true;
    });
}

builder.Host.UseConsoleLifetime();

builder.WebHost.ConfigureKestrel(options =>
{
    // The single most important setting in this file.
    //
    // Kestrel's default MinResponseDataRate is 240 bytes/s with a 5 second grace period.
    // One of the documented uses of this tool is `payload_size=0&interval=250ms` — a
    // metadata-only stream at roughly 200 bytes/s — which Kestrel would abort as too slow.
    // Leaving the default would therefore reject a workload the documented API contract
    // explicitly supports.
    options.Limits.MinResponseDataRate = null;

    // Request headers get 10s, an idle connection 120s and a request head 32KiB: generous
    // enough for a slow client, tight enough to bound one that never finishes. ReadTimeout
    // and WriteTimeout are deliberately left unset because both would cap the lifetime of a
    // streaming response; the per-frame write deadline is applied per frame instead, which
    // is what makes a long-lived stream possible at all.
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
    options.Limits.MaxRequestHeadersTotalSize = 32 << 10;
    options.Limits.MaxRequestBodySize = null;
    options.AddServerHeader = false;

    Listen(options, config);
});

var counters = new Counters();
var manager = new StreamManager(config.Limits, counters, TimeProvider.System);
var cors = new CorsPolicy(config.CorsAllowOrigins);

// The pacer needs a wait primitive accurate enough for the documented 10 ms interval floor.
// On Windows that means a high-resolution waitable timer: every portable .NET wait is
// quantised to the 15.625 ms system tick, which would deliver roughly two thirds of the
// promised frames at that interval. Measured, the high-resolution timer cuts the median
// deadline error from 6.9 ms to 0.4 ms. Elsewhere — or on a Windows build too old to offer
// the primitive — the portable strategy is used and short intervals are correspondingly
// coarse. That is a real accuracy limit rather than a silent one: which strategy is in force
// is reported at startup below.
IDelayStrategy delayStrategy = HighResolutionDelayStrategy.IsSupported
    ? new HighResolutionDelayStrategy()
    : TaskDelayStrategy.Instance;

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(counters);
builder.Services.AddSingleton(manager);
builder.Services.AddSingleton(cors);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(delayStrategy);
builder.Services.AddSingleton<ApiSurface>();

// Cross-origin access is the framework's job, configured from the same origin list the
// WebSocket handshake validates against.
builder.Services.AddCors(cors.ApplyTo);

// The drain budget is the whole shutdown budget, so the host is given a little more than
// that before it force-aborts — otherwise a slow drain would be cut short by the host
// rather than by SHUTDOWN_TIMEOUT.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = config.ShutdownTimeout + TimeSpan.FromSeconds(5));

var app = builder.Build();

// The payload document is process-wide and is swapped in before the server starts
// listening, so streams can read it without a lock. A broken file is a startup failure
// rather than a surprise in the middle of a test.
try
{
    PayloadDocument.LoadDocument(config.PayloadFile);
}
catch (PayloadDocumentException error)
{
    app.Logger.LogError("fatal error={Error}", error.Message);
    return 1;
}

WebUiAssets assets;
try
{
    assets = WebUiAssets.Load(typeof(WebUiAssets).Assembly);
}
catch (InvalidOperationException error)
{
    app.Logger.LogError("fatal error={Error}", error.Message);
    return 1;
}

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() => DrainAndClose(manager, config, app.Logger));

// Outermost first: a failure anywhere below becomes the shared JSON error body rather than
// a bare connection reset.
app.UseMiddleware<RecovererMiddleware>();

// Explicit, and not optional. WebApplication otherwise inserts UseRouting at the very front of
// the pipeline, which would put route selection above the trailing-slash guard below.
app.UseRouting();

// After routing, because CORS resolves the applicable policy from endpoint metadata. The
// policy is registered as the default, so it applies to every endpoint without opt-in.
//
// VaryOrigin runs first so the header is set before any handler can start the response, and
// covers the paths the framework's own middleware does not.
app.UseMiddleware<VaryOriginMiddleware>();
app.UseCors();

// Sits between Cors and WebSockets, and both neighbours matter.
//
// Below UseCors so a cross-origin caller reaches the guard with the CORS response headers
// already set. Placed above Cors instead, the rejection would be written before any of them
// existed, and a cross-origin client whose path-joining bug appended a slash would see an
// opaque CORS failure rather than the 405 that explains what went wrong — hiding the one
// diagnostic the guard exists to deliver.
//
// Above UseWebSockets because that middleware completes the handshake itself rather than at
// the endpoint, so a guard underneath it could no longer answer /api/stream/ws/ with anything
// but an upgrade.
//
// Routing having already matched is fine: UseRouting only selects an endpoint, and nothing
// runs it until the terminal middleware at the end of the pipeline. So /api/stream/http/ is
// still refused before the stream endpoint is ever entered, which is the property that matters.
app.UseMiddleware<ApiPathMiddleware>();

app.UseWebSockets();

// The read routes answer HEAD as well as GET. A monitor that wants only the headers of
// /api/health is making a reasonable request, but ASP.NET's MapGet does not match HEAD, so
// without this those probes collect a 405. This is an addition to the framework's behaviour
// rather than a use of it, and it is deliberate: the alternative is answering a well-formed
// request with a wrong answer.
string[] readMethods = [HttpMethods.Get, HttpMethods.Head];

app.MapMethods("/api/health", readMethods, (HttpContext context, ApiSurface api) => api.HealthAsync(context));
app.MapMethods("/api/info", readMethods, (HttpContext context, ApiSurface api) => api.InfoAsync(context));
app.MapMethods("/api/config", readMethods, (HttpContext context, ApiSurface api) => api.ConfigAsync(context));
app.MapMethods("/api/metrics", readMethods, (HttpContext context, ApiSurface api) => api.MetricsAsync(context));
app.MapMethods("/api/client", readMethods, (HttpContext context, ApiSurface api) => api.ClientAsync(context));

// The three streaming protocols. MapGet, not readMethods: a HEAD here would be answered by
// starting a real stream and suppressing the body, so a request that asked for no data would
// still hold a concurrency slot for up to an hour. Refusing it with 405 is the honest answer,
// and it is what the Allow header in ApiSurface already advertises.
app.MapGet("/api/stream/http", (
    HttpContext context, ServerConfig cfg, StreamManager mgr, Counters counters, IDelayStrategy delay, ILoggerFactory logs) =>
    NdjsonStreamEndpoint.HandleAsync(context, cfg, mgr, counters, delay, logs));

app.MapGet("/api/stream/sse", (
    HttpContext context, ServerConfig cfg, StreamManager mgr, Counters counters, IDelayStrategy delay, ILoggerFactory logs) =>
    SseStreamEndpoint.HandleAsync(context, cfg, mgr, counters, delay, logs));

app.MapGet("/api/stream/ws", (
    HttpContext context, ServerConfig cfg, StreamManager mgr, Counters counters, CorsPolicy policy, IDelayStrategy delay, ILoggerFactory logs) =>
    WebSocketStreamEndpoint.HandleAsync(context, cfg, mgr, counters, policy, delay, logs));

// Unknown /api paths must never fall through to the SPA. This route is less specific than
// the literals above, so it only sees leftovers.
app.Map("/api/{**rest}", (HttpContext context, ApiSurface api) => api.UnknownAsync(context));

// Front end last: a fallback endpoint, so every route above wins by construction.
app.MapWebUi(assets, app.Logger);

app.Logger.LogInformation(
    "server start addr={Addr} version={Version} max_duration={MaxDuration} min_interval={MinInterval} max_payload_size={MaxPayloadSize} max_concurrent_streams={MaxConcurrentStreams} write_timeout={WriteTimeout} shutdown_timeout={ShutdownTimeout} cors_allow_origins={Cors} payload_document_bytes={PayloadBytes} payload_file={PayloadFile}",
    config.Address,
    ServerConfig.ServiceVersion,
    DurationText.Format(config.Limits.MaxDuration),
    DurationText.Format(config.Limits.MinInterval),
    config.Limits.MaxPayloadSize,
    config.Limits.MaxConcurrentStreams,
    DurationText.Format(config.Limits.WriteTimeout),
    DurationText.Format(config.ShutdownTimeout),
    config.CorsAllowOrigins.Count == 0 ? "(none)" : string.Join(",", config.CorsAllowOrigins),
    PayloadDocument.Size,
    string.IsNullOrEmpty(config.PayloadFile) ? "(embedded)" : config.PayloadFile);

app.Logger.LogInformation(
    "timer pacer={Pacer} min_interval={MinInterval}",
    delayStrategy is HighResolutionDelayStrategy ? "high-resolution (accurate at the 10ms floor)" : "portable (quantised to the ~15.6ms system tick)",
    DurationText.Format(config.Limits.MinInterval));

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>
/// Binds the listen socket for the configured host and port, with an empty host meaning
/// every interface and a hostname resolved before binding.
/// </summary>
static void Listen(KestrelServerOptions options, ServerConfig config)
{
    if (config.Host.Length == 0)
    {
        // An empty host means every interface, IPv4 and IPv6 alike, and ListenAnyIP is the
        // single call that binds both.
        options.ListenAnyIP(config.Port);
        return;
    }

    if (string.Equals(config.Host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        options.ListenLocalhost(config.Port);
        return;
    }

    if (IPAddress.TryParse(config.Host, out var address))
    {
        if (address.Equals(IPAddress.Any))
        {
            options.ListenAnyIP(config.Port);
        }
        else
        {
            options.Listen(address, config.Port);
        }

        return;
    }

    // A hostname is not an address Kestrel can bind, so it is resolved here; an empty
    // answer is a startup failure rather than a socket bound to nothing.
    var resolved = Dns.GetHostAddresses(config.Host);
    if (resolved.Length == 0)
    {
        throw new InvalidOperationException($"cannot resolve HOST \"{config.Host}\"");
    }

    options.Listen(resolved[0], config.Port);
}

/// <summary>
/// The graceful shutdown sequence.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>stop accepting new streams, so the manager rejects them with 503;</item>
/// <item>wait up to SHUTDOWN_TIMEOUT for in-flight streams to finish on their own;</item>
/// <item>cancel whatever is left — which closes WebSockets with a going-away frame and ends
/// the NDJSON and SSE responses — and let Kestrel close the sockets.</item>
/// </list>
/// This runs from the host's stopping callback, which the host invokes before it stops
/// Kestrel, so requests arriving during the drain are still served and cleanly rejected.
/// </remarks>
static void DrainAndClose(StreamManager manager, ServerConfig config, ILogger logger)
{
    manager.StartDraining();
    logger.LogInformation(
        "shutdown: draining active_streams={Active} timeout={Timeout}",
        manager.Active,
        DurationText.Format(config.ShutdownTimeout));

    using var deadline = new CancellationTokenSource(config.ShutdownTimeout);
    try
    {
        manager.DrainAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
        logger.LogInformation("shutdown: streams drained");
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning(
            "shutdown: drain timeout reached, cancelling remaining streams active_streams={Active}",
            manager.Active);
    }
    catch (Exception error)
    {
        logger.LogInformation(
            "shutdown: drain stopped reason={Reason} active_streams={Active}",
            error.Message,
            manager.Active);
    }

    // Cancelling is a no-op when everything has already finished.
    manager.ForceClose();

    var snapshot = manager.TakeSnapshot();
    logger.LogInformation(
        "server shutdown complete streams_started={Started} streams_finished={Finished} frames_sent={Frames} bytes_sent={Bytes}",
        snapshot.StreamsStarted,
        snapshot.StreamsFinished,
        snapshot.FramesSent,
        snapshot.BytesSent);
}

/// <summary>
/// Exists so <c>WebApplicationFactory&lt;Program&gt;</c> can name the entry point.
/// </summary>
/// <remarks>
/// Top-level statements compile into an internal <c>Program</c>, which a test assembly
/// cannot reference. Declaring the partial here makes the same type public without moving
/// the composition root out of the file that owns it.
/// </remarks>
public partial class Program;
