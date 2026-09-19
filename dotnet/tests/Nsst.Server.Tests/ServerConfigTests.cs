using Microsoft.Extensions.Logging;
using Nsst.Server;
using Xunit;

namespace Nsst.Server.Tests;

/// <summary>
/// Covers the environment → <see cref="ServerConfig"/> mapping.
/// </summary>
/// <remarks>
/// Every test drives <see cref="ServerConfigLoader.LoadFrom"/> with a dictionary rather than
/// mutating the process environment: a test that edits real environment variables cannot run
/// alongside any other test.
/// </remarks>
public sealed class ServerConfigTests
{
    private static ServerConfig Load(params (string Key, string Value)[] pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            values[key] = value;
        }

        return ServerConfigLoader.LoadFrom(key => values.GetValueOrDefault(key));
    }

    [Fact]
    public void AnEmptyEnvironmentYieldsTheDocumentedDefaults()
    {
        var config = Load();

        Assert.Equal(string.Empty, config.Host);
        Assert.Equal(8080, config.Port);
        Assert.Empty(config.CorsAllowOrigins);
        Assert.Equal(LogLevel.Information, config.LogLevel);
        Assert.Equal("text", config.LogFormat);
        Assert.Equal(TimeSpan.FromSeconds(15), config.ShutdownTimeout);
        Assert.Equal(string.Empty, config.PayloadFile);

        // Defaults that clients depend on: these are the numbers /api/config reports and that
        // a stream started with no query string is validated against.
        Assert.Equal(TimeSpan.FromSeconds(1), config.Limits.MinDuration);
        Assert.Equal(TimeSpan.FromSeconds(3600), config.Limits.MaxDuration);
        Assert.Equal(TimeSpan.FromSeconds(60), config.Limits.DefaultDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(10), config.Limits.MinInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(60000), config.Limits.MaxInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(100), config.Limits.DefaultInterval);
        Assert.Equal(1 << 20, config.Limits.MaxPayloadSize);
        Assert.Equal(80, config.Limits.DefaultPayloadSize);
        Assert.Equal(100, config.Limits.MaxConcurrentStreams);
        Assert.Equal(TimeSpan.FromMilliseconds(15000), config.Limits.WriteTimeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void RejectsAPortOutsideOneToSixtyFiveThousandFiveHundredThirtyFive(string port)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("PORT", port)));
        Assert.Contains("PORT", error.Message);
    }

    [Fact]
    public void AcceptsTheHighestLegalPort()
    {
        Assert.Equal(65535, Load(("PORT", "65535")).Port);
    }

    [Fact]
    public void SurroundingWhitespaceIsIgnored()
    {
        // Leading and trailing whitespace is trimmed before parsing, so a value pasted
        // out of a YAML file or a shell export still works.
        Assert.Equal(9000, Load(("PORT", "  9000  ")).Port);
    }

    [Fact]
    public void RejectsAnUnknownLogFormat()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("LOG_FORMAT", "xml")));
        Assert.Contains("LOG_FORMAT", error.Message);
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFO", LogLevel.Information)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    public void MapsEveryAcceptedLogLevelAndIsCaseInsensitive(string raw, LogLevel expected)
    {
        Assert.Equal(expected, Load(("LOG_LEVEL", raw)).LogLevel);
    }

    [Fact]
    public void RejectsAnUnknownLogLevel()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Load(("LOG_LEVEL", "trace")));
        Assert.Contains("LOG_LEVEL", error.Message);
    }

    [Fact]
    public void SplitsTheCorsListAndDropsEmptyEntries()
    {
        var config = Load(("CORS_ALLOW_ORIGINS", " https://a.example ,, https://b.example ,"));

        Assert.Equal(["https://a.example", "https://b.example"], config.CorsAllowOrigins);
    }

    [Fact]
    public void RejectsAMaxIntervalBelowTheMinimum()
    {
        // Each value is individually legal; only the relationship between them is not.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Load(("MIN_INTERVAL", "5000"), ("MAX_INTERVAL", "1000")));

        Assert.Contains("MAX_INTERVAL", error.Message);
        Assert.Contains("MIN_INTERVAL", error.Message);
    }

    [Fact]
    public void ClampsTheAdvertisedDefaultsIntoTheConfiguredBounds()
    {
        // The defaults published by /api/config have to remain legal requests, so a bounds
        // change drags them along rather than leaving them out of range.
        var config = Load(
            ("MIN_INTERVAL", "2000"),
            ("MAX_INTERVAL", "3000"),
            ("MAX_PAYLOAD_SIZE", "16"));

        Assert.Equal(TimeSpan.FromMilliseconds(2000), config.Limits.DefaultInterval);
        Assert.Equal(16, config.Limits.DefaultPayloadSize);
    }

    [Fact]
    public void ReportsEveryProblemRatherThanOnlyTheFirst()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Load(("PORT", "0"), ("LOG_FORMAT", "xml")));

        Assert.Contains("PORT", error.Message);
        Assert.Contains("LOG_FORMAT", error.Message);
    }

    [Fact]
    public void KeepsThePayloadFileVerbatim()
    {
        // Not trimmed beyond the surrounding whitespace: the path is passed straight to the
        // loader, and silently rewriting it would hide a typo.
        Assert.Equal("/etc/nsst/payload.txt", Load(("PAYLOAD_FILE", " /etc/nsst/payload.txt ")).PayloadFile);
    }
}
