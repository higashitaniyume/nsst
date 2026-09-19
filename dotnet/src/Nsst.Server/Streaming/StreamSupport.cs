using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Nsst.Core.Streaming;

namespace Nsst.Server.Streaming;

/// <summary>Adapts ASP.NET's query collection to the interface <c>Nsst.Core</c> parses.</summary>
/// <remarks>
/// Keeping the parser behind <see cref="IQuery"/> is what lets the parameter tests run
/// without an HTTP stack, while the endpoint still validates through the very same code
/// path the real server uses.
/// </remarks>
internal sealed class QueryAdapter(IQueryCollection query) : IQuery
{
    private readonly IQueryCollection _query = query;

    public bool Has(string name) => _query.ContainsKey(name);

    public string? First(string name) =>
        _query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}

/// <summary>
/// Applies the per-frame write deadline.
/// </summary>
/// <remarks>
/// ASP.NET Core has no per-write deadline on <c>IHttpResponseBodyFeature</c>, so this arms
/// <c>IConnectionTimeoutFeature</c> instead, which aborts the connection if the frame has
/// not been flushed in time.
/// <para>
/// What matters is the observable behaviour for a stalled reader: the write fails instead
/// of blocking forever, and because the stream's own cancellation token is still untouched,
/// the runner classifies it as a write error rather than a client disconnect.
/// </para>
/// <para>
/// The feature is absent under some hosts — notably the in-memory test server — in which
/// case <see cref="Supported"/> is false and a stalled reader is bounded only by the
/// peer's own cancellation. That is recorded rather than silently ignored.
/// </para>
/// </remarks>
internal sealed class FrameWriteTimeout
{
    private readonly IConnectionTimeoutFeature? _feature;
    private readonly TimeSpan _timeout;

    public FrameWriteTimeout(HttpContext context, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(context);

        _feature = context.Features.Get<IConnectionTimeoutFeature>();
        _timeout = timeout;
    }

    /// <summary>Whether a real deadline is in force for this response.</summary>
    public bool Supported => _feature is not null;

    public void Arm() => _feature?.SetTimeout(_timeout);

    public void Disarm() => _feature?.CancelTimeout();
}

/// <summary>Writes the canonical per-stream lifecycle log entries.</summary>
/// <remarks>
/// Per-frame details are deliberately not logged: a long test emits hundreds of thousands
/// of frames and would drown the log.
/// </remarks>
internal static class StreamLog
{
    public static void Started(
        ILogger logger,
        string protocol,
        string remote,
        ulong id,
        StreamParams parameters)
    {
        logger.LogInformation(
            "stream started stream_id={StreamId} protocol={Protocol} remote={Remote} duration={Duration} interval={Interval} payload_size={PayloadSize} expected_frames={ExpectedFrames}",
            id,
            protocol,
            remote,
            DurationText.Format(parameters.Duration),
            DurationText.Format(parameters.Interval),
            parameters.PayloadSize,
            parameters.ExpectedFrames);
    }

    public static void Finished(
        ILogger logger,
        string protocol,
        string remote,
        ulong id,
        StreamParams parameters,
        StreamResult result)
    {
        var error = result.Error?.Message ?? string.Empty;

        switch (result.Reason)
        {
            case EndReason.Completed:
                logger.LogInformation(
                    "stream completed stream_id={StreamId} protocol={Protocol} remote={Remote} duration={Duration} interval={Interval} payload_size={PayloadSize} frames={Frames} bytes={Bytes} elapsed={Elapsed} reason={Reason}",
                    id, protocol, remote,
                    DurationText.Format(parameters.Duration),
                    DurationText.Format(parameters.Interval),
                    parameters.PayloadSize,
                    result.Frames,
                    result.BytesSent,
                    DurationText.Format(Round(result.Elapsed, TimeSpan.FromMilliseconds(1))),
                    result.Reason.ToWireValue());
                break;

            case EndReason.WriteError:
                logger.LogWarning(
                    "stream error stream_id={StreamId} protocol={Protocol} remote={Remote} duration={Duration} interval={Interval} payload_size={PayloadSize} frames={Frames} bytes={Bytes} elapsed={Elapsed} reason={Reason} error={Error}",
                    id, protocol, remote,
                    DurationText.Format(parameters.Duration),
                    DurationText.Format(parameters.Interval),
                    parameters.PayloadSize,
                    result.Frames,
                    result.BytesSent,
                    DurationText.Format(Round(result.Elapsed, TimeSpan.FromMilliseconds(1))),
                    result.Reason.ToWireValue(),
                    error);
                break;

            default:
                logger.LogInformation(
                    "stream cancelled stream_id={StreamId} protocol={Protocol} remote={Remote} duration={Duration} interval={Interval} payload_size={PayloadSize} frames={Frames} bytes={Bytes} elapsed={Elapsed} reason={Reason} cause={Cause}",
                    id, protocol, remote,
                    DurationText.Format(parameters.Duration),
                    DurationText.Format(parameters.Interval),
                    parameters.PayloadSize,
                    result.Frames,
                    result.BytesSent,
                    DurationText.Format(Round(result.Elapsed, TimeSpan.FromMilliseconds(1))),
                    result.Reason.ToWireValue(),
                    error);
                break;
        }
    }

    /// <summary>
    /// Rounds to the nearest multiple, halves away from zero, so an elapsed time lands on
    /// the millisecond grid the log line is read at.
    /// </summary>
    private static TimeSpan Round(TimeSpan value, TimeSpan multiple)
    {
        var ticks = value.Ticks;
        var unit = multiple.Ticks;
        if (unit <= 0)
        {
            return value;
        }

        var remainder = ticks % unit;
        var rounded = ticks - remainder;
        if (remainder * 2 >= unit)
        {
            rounded += unit;
        }
        else if (remainder * 2 <= -unit)
        {
            rounded -= unit;
        }

        return TimeSpan.FromTicks(rounded);
    }
}

/// <summary>Parses stream parameters and reserves a concurrency slot, or writes the error.</summary>
internal static class StreamRequest
{
    /// <summary>A validated request that has been granted a stream slot.</summary>
    internal readonly record struct Start(StreamParams Parameters, StreamLease Lease);

    /// <summary>
    /// Validates the query string and acquires a lease.
    /// </summary>
    /// <returns>
    /// Null when the request was rejected, in which case the response has already been
    /// written. The caller must not write anything.
    /// </returns>
    public static async Task<Start?> BeginAsync(
        HttpContext context,
        Limits limits,
        StreamManager manager,
        CancellationToken parent)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(manager);

        StreamParams parameters;
        try
        {
            parameters = ParamsParser.Parse(new QueryAdapter(context.Request.Query), limits);
        }
        catch (ParamException error)
        {
            // The full sentence goes in "message" and the parameter name in "param", which
            // is what the console highlights in the form.
            await Http.HttpResponses.WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                Http.ErrorCodes.InvalidParameter,
                error.Message,
                error.Param).ConfigureAwait(false);
            return null;
        }

        try
        {
            return new Start(parameters, manager.Acquire(parent));
        }
        catch (StreamRejectedException error)
        {
            await Http.HttpResponses.WriteAcquireErrorAsync(context, error).ConfigureAwait(false);
            return null;
        }
    }
}

/// <summary>
/// Presents a combined cancellation token under the lease's end reason.
/// </summary>
/// <remarks>
/// The WebSocket endpoint cancels its stream when the peer closes, which the lease knows
/// nothing about. Only the reason has to come from the lease: if the server is shutting
/// down the reason is <see cref="EndReason.ServerShutdown"/>, and otherwise a cancelled
/// token means the peer went away.
/// </remarks>
internal sealed class LinkedLifetime(CancellationToken combined, IStreamLifetime inner) : IStreamLifetime
{
    private readonly CancellationToken _combined = combined;
    private readonly IStreamLifetime _inner = inner;

    public CancellationToken Cancellation => _combined;

    public EndReason CancellationReason => _inner.CancellationReason;
}
