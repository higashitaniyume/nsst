namespace Nsst.Core.Streaming;

/// <summary>
/// Why an emit loop stopped.
/// </summary>
/// <remarks>
/// The spellings are a log format contract: they appear as the <c>reason</c> field
/// of every stream lifecycle entry, and operators grep and alert on them. They are
/// produced only by <see cref="EndReasonExtensions.ToWireValue"/>, so that is the
/// one place a rename would have to start.
/// </remarks>
public enum EndReason
{
    /// <summary>The requested duration elapsed and every scheduled frame was written.</summary>
    Completed,

    /// <summary>The peer went away: request cancelled, TCP reset, WebSocket close frame.</summary>
    ClientClosed,

    /// <summary>
    /// A write failed while the connection was still expected to be alive — most
    /// often the per frame write deadline expired because the client stopped
    /// reading.
    /// </summary>
    WriteError,

    /// <summary>The server began shutting down and the drain deadline cut the stream short.</summary>
    ServerShutdown,
}

public static class EndReasonExtensions
{
    /// <summary>The spelling used in logs and in <c>EndReason</c> comparisons.</summary>
    public static string ToWireValue(this EndReason reason) => reason switch
    {
        EndReason.Completed => "completed",
        EndReason.ClientClosed => "client_closed",
        EndReason.WriteError => "write_error",
        EndReason.ServerShutdown => "server_shutdown",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "unknown end reason"),
    };
}
