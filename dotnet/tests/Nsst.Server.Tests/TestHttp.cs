using System.Net;
using Microsoft.AspNetCore.Http;

namespace Nsst.Server.Tests;

/// <summary>Builds the minimal <see cref="HttpContext"/> the pure helpers need.</summary>
internal static class TestHttp
{
    /// <summary>
    /// A context with the given socket peer and no headers.
    /// </summary>
    /// <param name="remote">
    /// The peer address in literal form, or <see langword="null"/> to leave it unset — which
    /// is what Kestrel reports for an in-memory or already-disconnected connection.
    /// </param>
    /// <param name="port">The peer port.</param>
    internal static HttpContext Context(string? remote, int port)
    {
        var context = new DefaultHttpContext();
        if (remote is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        }

        context.Connection.RemotePort = port;
        return context;
    }
}
