using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Nsst.Core.Metrics;
using Nsst.Server.Api;
using Xunit;

namespace Nsst.Server.Tests;

/// <summary>
/// Covers the HTTP surface itself: which methods each route answers, what a trailing slash
/// does, and where an unknown path lands.
/// </summary>
/// <remarks>
/// <para>
/// These boot the real <c>Program.cs</c> through <see cref="WebApplicationFactory{TEntryPoint}"/>,
/// so they exercise the shipping middleware order rather than a re-creation of it. Order is the
/// entire point of two of these cases — the trailing-slash guard has to run above routing, and
/// nothing in the type system enforces that.
/// </para>
/// <para>
/// Every client here disables automatic redirects. That is not incidental: a followed redirect
/// looks exactly like a success, and an earlier round of this work mistook a <c>307</c> for a
/// direct <c>200</c> and locked the difference in. A test that cannot see a redirect cannot
/// fail on one.
/// </para>
/// </remarks>
public sealed class RoutingTests
{
    /// <summary>
    /// Creates a client that surfaces redirects instead of following them.
    /// </summary>
    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Runs requests against a throwaway host.
    /// </summary>
    /// <remarks>
    /// One host per case rather than a shared fixture, because the counter assertions below
    /// are only meaningful against a process that has served nothing else.
    /// </remarks>
    private static async Task WithServerAsync(Func<HttpClient, IServiceProvider, Task> body)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = NewClient(factory);

        // Touching the client forces the host to start, which is what builds the endpoint list.
        await body(client, factory.Services);
    }

    [Fact]
    public async Task EveryRouteIsMappedWithTheMethodsItAdvertises()
    {
        // The guard the KnownRoutes comment promises. That table drives the Allow header a
        // client sees on a 405, and it is hand-written, so without this it is free to drift
        // away from the routes Program actually maps — turning a 405 into a 404 and nobody
        // noticing until a client tries to negotiate.
        await WithServerAsync(async (client, services) =>
        {
            using var warmup = await client.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);

            var mapped = MappedApiRoutes(services);

            Assert.Equal(ApiSurface.KnownRoutes.Keys, mapped.Keys);
            foreach (var (path, advertised) in ApiSurface.KnownRoutes)
            {
                Assert.Equal(advertised, mapped[path]);
            }
        });
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/info")]
    [InlineData("/api/config")]
    [InlineData("/api/metrics")]
    [InlineData("/api/client")]
    public async Task HeadIsAnsweredOnTheReadRoutes(string path)
    {
        // A monitor asking for headers only is making a reasonable request, and MapGet does not
        // match HEAD — so this is behaviour that had to be added deliberately.
        await WithServerAsync(async (client, _) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
    }

    [Theory]
    [InlineData("/api/stream/http")]
    [InlineData("/api/stream/sse")]
    [InlineData("/api/stream/ws")]
    public async Task HeadOnAStreamRouteIsRefusedWithoutStartingAStream(string path)
    {
        // The alternative is worse than a 405: answering HEAD by running the stream would hold
        // a concurrency slot for the full duration while sending no body, so a client that
        // explicitly asked for no data could deny service to everyone else.
        await WithServerAsync(async (client, services) =>
        {
            var counters = services.GetRequiredService<Counters>();

            using var request = new HttpRequestMessage(HttpMethod.Head, path);
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Contains("GET", response.Content.Headers.Allow);
            Assert.DoesNotContain("HEAD", response.Content.Headers.Allow);

            Assert.Equal(0UL, counters.StreamsStarted);
            Assert.Equal(0L, counters.ActiveStreams);
        });
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/config")]
    [InlineData("/api/stream/http")]
    [InlineData("/api/stream/sse")]
    [InlineData("/api/stream/ws")]
    public async Task ATrailingSlashIsRefusedInsteadOfBeingTreatedAsTheRoute(string path)
    {
        // ASP.NET treats /api/stream/http and /api/stream/http/ as the same route. Left alone,
        // a path-joining bug or a proxy rule would start a real stream and hold a slot for it —
        // one mistyped URL enough to deny service at the configured limit. The counters are the
        // load-bearing assertion here; the status code alone would not prove nothing ran.
        await WithServerAsync(async (client, services) =>
        {
            var counters = services.GetRequiredService<Counters>();

            using var response = await client.GetAsync(path + "/");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(
                ApiSurface.KnownRoutes[path],
                string.Join(", ", response.Content.Headers.Allow.OrderBy(m => m, StringComparer.Ordinal)));

            Assert.Equal(0UL, counters.StreamsStarted);
            Assert.Equal(0L, counters.ActiveStreams);
        });
    }

    [Fact]
    public async Task AWrongMethodOnAKnownRouteIsMethodNotAllowed()
    {
        await WithServerAsync(async (client, _) =>
        {
            using var response = await client.PostAsync("/api/health", content: null);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Contains("GET", response.Content.Headers.Allow);
        });
    }

    [Fact]
    public async Task AnUnknownApiPathIsNotFoundRatherThanFallingThroughToTheConsole()
    {
        // A mistyped API path that returned the HTML console instead would be far harder to
        // diagnose than a 404, because the caller would be parsing markup as JSON.
        await WithServerAsync(async (client, _) =>
        {
            using var response = await client.GetAsync("/api/no-such-endpoint");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        });
    }

    [Fact]
    public async Task ANonApiPathFallsThroughToTheConsole()
    {
        await WithServerAsync(async (client, _) =>
        {
            using var response = await client.GetAsync("/some/deep/client/side/route");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        });
    }

    /// <summary>
    /// Reads the API routes out of the running host as <c>"GET, HEAD, OPTIONS"</c> strings.
    /// </summary>
    /// <remarks>
    /// <c>OPTIONS</c> is appended because the CORS middleware answers a preflight before
    /// routing ever sees it, so no endpoint carries it in its own metadata — but it is still a
    /// method the route accepts as far as any client is concerned, which is what the advertised
    /// string describes.
    /// </remarks>
    private static SortedDictionary<string, string> MappedApiRoutes(IServiceProvider services)
    {
        var routes = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            var pattern = route.RoutePattern.RawText;
            if (pattern is null
                || !pattern.StartsWith("/api/", StringComparison.Ordinal)
                || pattern.Contains('{', StringComparison.Ordinal))
            {
                // Skips the /api/{**rest} catch-all and the console's own fallback, which are
                // not part of the advertised surface.
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            routes[pattern] = string.Join(
                ", ",
                methods.Append("OPTIONS").Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal));
        }

        return routes;
    }
}
