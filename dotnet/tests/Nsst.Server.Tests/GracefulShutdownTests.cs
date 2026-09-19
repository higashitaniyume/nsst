using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Server.Tests;

/// <summary>
/// Covers the shutdown path end to end: not <see cref="StreamManager"/>'s drain in
/// isolation, which <c>StreamManagerTests</c> already pins, but the wiring from the host's
/// stopping signal through <c>DrainAndClose</c> to a real HTTP response that has to be
/// allowed to finish.
/// </summary>
/// <remarks>
/// <para>
/// These boot the actual <c>Program.cs</c> through <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// so the composition root, middleware order and endpoint mapping under test are the
/// shipping ones rather than a re-creation of them.
/// </para>
/// <para>
/// <see cref="IHostApplicationLifetime.StopApplication"/> is the same call the console
/// lifetime makes from its SIGINT / SIGTERM / CTRL+C handler, so this exercises everything
/// except the operating system's signal delivery — which is framework code and cannot be
/// reached from an in-process test. Windows has no SIGTERM, and sending CTRL+BREAK to a
/// process group needs <c>CreateProcess</c> with <c>CREATE_NEW_PROCESS_GROUP</c>, which
/// <c>Process.Start</c> cannot express.
/// </para>
/// </remarks>
public sealed class GracefulShutdownTests
{
    [Fact]
    public async Task RefusesANewStreamOnceDrainingHasStarted()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<StreamManager>().StartDraining();

        using var response = await client.GetAsync("/api/stream/http?duration=1&interval=100");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // A rejected client needs a retry hint, and it needs it in the header a proxy already
        // understands — the status code alone would let a proxy hammer the server.
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task LetsAnInFlightStreamFinishBeforeTheHostStops()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var frames = new ConcurrentQueue<string>();

        // The reader has to run on its own task. StopApplication runs the drain
        // synchronously on the calling thread, so if the reader were that same thread it
        // would stop reading, the response buffer would fill, the writer would block on
        // flow control, and the drain would end in its timeout instead of a clean finish.
        var reader = Task.Run(async () =>
        {
            using var response = await client.GetAsync(
                "/api/stream/http?duration=1&interval=100&payload_size=64",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var body = await response.Content.ReadAsStreamAsync();
            using var text = new StreamReader(body);
            while (await text.ReadLineAsync() is { } line)
            {
                if (line.Length > 0)
                {
                    frames.Enqueue(line);
                }
            }
        });

        // Let the stream be genuinely in flight before signalling; stopping before the first
        // frame would prove nothing about draining.
        var start = DateTime.UtcNow.AddSeconds(10);
        while (frames.Count < 2 && DateTime.UtcNow < start)
        {
            await Task.Delay(20);
        }

        Assert.True(frames.Count >= 2, $"the stream never started; saw {frames.Count} frame(s)");

        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await reader.WaitAsync(TimeSpan.FromSeconds(30));

        // duration=1s at interval=100ms is exactly 10 frames. Getting all ten is the whole
        // point: the drain waited for the stream rather than truncating it.
        Assert.Equal(10, frames.Count);
    }
}
