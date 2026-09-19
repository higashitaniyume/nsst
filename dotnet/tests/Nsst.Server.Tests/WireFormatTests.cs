using Nsst.Server.Api;
using Nsst.Server.Http;
using Nsst.Server.Streaming;
using Nsst.Server.WebUi;
using Xunit;

namespace Nsst.Server.Tests;

/// <summary>
/// Covers the small pure functions whose output is not checked by the type system.
/// </summary>
/// <remarks>
/// These are the places where a regression would not throw — it would just quietly put a
/// different string on the wire or in a log line. A table here covers the whole range, which
/// an end-to-end test only samples.
/// </remarks>
public sealed class WireFormatTests
{
    [Theory]
    [InlineData(0L, "0")]
    [InlineData(100L, "100ns")]                      // one tick
    [InlineData(1_000L, "1µs")]
    [InlineData(1_500L, "1.5µs")]
    [InlineData(1_000_000L, "1ms")]
    [InlineData(10_000_000L, "10ms")]
    [InlineData(250_000_000L, "250ms")]
    [InlineData(1_000_000_000L, "1s")]
    [InlineData(1_500_000_000L, "1.5s")]
    [InlineData(60_000_000_000L, "1m0s")]
    [InlineData(90_000_000_000L, "1m30s")]
    [InlineData(3_600_000_000_000L, "1h0m0s")]
    [InlineData(5_400_000_000_000L, "1h30m0s")]
    public void FormatsADurationCompactly(long nanoseconds, string expected)
    {
        // TimeSpan ticks are 100 ns, which is the resolution these strings are printed at for
        // every value the config can express.
        var value = TimeSpan.FromTicks(nanoseconds / 100);

        Assert.Equal(expected, DurationText.Format(value));
    }

    [Fact]
    public void PreservesTheSignOfANegativeDuration()
    {
        Assert.Equal("-1s", DurationText.Format(TimeSpan.FromSeconds(-1)));
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("index-uQuuyqMM.css", "text/css")]
    [InlineData("app.js", "text/javascript")]
    public void AppendsACharsetToTextAssets(string name, string expectedType)
    {
        // Without the charset a browser is left to guess the encoding of the console's own
        // markup, and it may guess wrong.
        Assert.Equal(expectedType + "; charset=utf-8", WebUiEndpoints.ContentTypeFor(name));
    }

    [Theory]
    [InlineData("favicon.svg")]
    [InlineData("font.woff2")]
    public void LeavesBinaryAssetsAlone(string name)
    {
        // A charset on a font or an image is meaningless at best.
        Assert.DoesNotContain("charset", WebUiEndpoints.ContentTypeFor(name), StringComparison.Ordinal);
        Assert.NotEqual("application/octet-stream", WebUiEndpoints.ContentTypeFor(name));
    }

    [Fact]
    public void FallsBackForAnUnknownExtension()
    {
        Assert.Equal("application/octet-stream", WebUiEndpoints.ContentTypeFor("mystery.zzz"));
        Assert.Equal("application/octet-stream", WebUiEndpoints.ContentTypeFor("no-extension"));
    }

    [Fact]
    public void ResolvesEveryAssetTheConsoleActuallyShips()
    {
        // The real regression this guards: a new asset type appears in the bundle and the
        // framework's table does not know it, so the browser downloads it instead of running
        // it. That only shows up on a clean deploy, which is exactly when it is expensive.
        var assets = WebUiAssets.Load(typeof(WebUiAssets).Assembly);

        Assert.NotEmpty(assets.Names);
        foreach (var name in assets.Names)
        {
            Assert.NotEqual("application/octet-stream", WebUiEndpoints.ContentTypeFor(name));
        }
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4:5678", "1.2.3.4")]
    [InlineData(" 1.2.3.4 ", "1.2.3.4")]
    [InlineData("[::1]", "::1")]
    [InlineData("[::1]:5678", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("not-an-address", "")]
    [InlineData("1.2.3.4:not-a-port", "")]
    public void NormalizesAProxyAddressToABareIp(string raw, string expected)
    {
        Assert.Equal(expected, ClientIp.NormalizeIp(raw));
    }

    [Fact]
    public void PrefersTheMostSpecificProxyHeader()
    {
        var context = TestHttp.Context(remote: "10.0.0.9", port: 1234);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.1, 10.0.0.1";
        context.Request.Headers["X-Real-IP"] = "203.0.113.2";
        context.Request.Headers["True-Client-IP"] = "203.0.113.3";
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.4";

        var (ip, source) = ClientIp.Resolve(context);

        Assert.Equal("203.0.113.4", ip);
        Assert.Equal(ClientIp.SourceCfConnectingIp, source);
    }

    [Fact]
    public void FallsBackThroughTheHeaderChainInOrder()
    {
        var context = TestHttp.Context(remote: "10.0.0.9", port: 1234);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.1";
        context.Request.Headers["X-Real-IP"] = "203.0.113.2";

        Assert.Equal(
            ("203.0.113.2", ClientIp.SourceXRealIp),
            ClientIp.Resolve(context));

        context.Request.Headers.Remove("X-Real-IP");
        Assert.Equal(
            ("203.0.113.1", ClientIp.SourceXForwardedFor),
            ClientIp.Resolve(context));

        context.Request.Headers.Remove("X-Forwarded-For");
        Assert.Equal(
            ("10.0.0.9", ClientIp.SourceRemoteAddr),
            ClientIp.Resolve(context));
    }

    [Fact]
    public void TakesTheOldestHopFromAForwardedChainAndDropsUnparsableEntries()
    {
        var context = TestHttp.Context(remote: "10.0.0.9", port: 1234);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.1, garbage, 10.0.0.1";

        Assert.Equal(["203.0.113.1", "10.0.0.1"], ClientIp.ForwardedFor(context));
    }

    [Fact]
    public void RebuildsThePeerInHostPortForm()
    {
        // /api/client exposes the "host:port" spelling, with an IPv6 host bracketed.
        // The console renders this string directly.
        Assert.Equal("1.2.3.4:5678", ClientIp.RemoteAddr(TestHttp.Context(remote: "1.2.3.4", port: 5678)));
        Assert.Equal("[::1]:5678", ClientIp.RemoteAddr(TestHttp.Context(remote: "::1", port: 5678)));
    }

    [Fact]
    public void UnmapsAnIpv4PeerReportedAsIpv6()
    {
        Assert.Equal("1.2.3.4:5678", ClientIp.RemoteAddr(TestHttp.Context(remote: "::ffff:1.2.3.4", port: 5678)));
    }

    [Fact]
    public void ReportsAnAbsentPeerAsEmpty()
    {
        Assert.Equal(string.Empty, ClientIp.RemoteAddr(TestHttp.Context(remote: null, port: 0)));
    }

    [Theory]
    [InlineData("HTTP/2", "HTTP/2.0")]
    [InlineData("HTTP/2.0", "HTTP/2.0")]
    [InlineData("HTTP/1.1", "HTTP/1.1")]
    [InlineData("HTTP/3", "HTTP/3.0")]
    [InlineData("HTTP/1", "HTTP/1.0")]
    [InlineData("SPDY/3", "SPDY/3")]
    public void RepairsAProtocolStringThatOmitsItsMinorVersion(string raw, string expected)
    {
        // Kestrel reports "HTTP/2"; the console and web/src/types.ts say "HTTP/2.0", and the
        // value is both displayed and exported to CSV. A missing minor version therefore has to
        // be repaired rather than passed through. A non-HTTP protocol is left alone.
        Assert.Equal(expected, ApiSurface.NormalizeProtocol(raw));
    }
}
