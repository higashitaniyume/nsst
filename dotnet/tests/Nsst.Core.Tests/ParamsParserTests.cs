using System.Globalization;
using Nsst.Core.Streaming;
using Xunit;

namespace Nsst.Core.Tests;

/// <summary>
/// Asserts that request validation matches the frozen golden fixture exactly,
/// including the text of every rejection.
/// </summary>
/// <remarks>
/// The rejection strings are contract, not diagnostics: the API layer puts them
/// straight into the JSON error body an operator reads when a script fails. Each
/// case in the fixture is therefore part of the observable API surface, not a
/// leftover of how it was produced.
/// </remarks>
public sealed class ParamsParserTests
{
    private static string GoldenPath => Path.Combine(AppContext.BaseDirectory, "golden", "params.txt");

    [Fact]
    public void MatchesTheGoldenFileForEveryQuery()
    {
        var limits = Limits.Default;
        var accepted = 0;
        var rejected = 0;

        foreach (var line in File.ReadLines(GoldenPath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var fields = line.Split('\t');
            var query = fields[1];

            switch (fields[0])
            {
                case "ok":
                    {
                        var expected = ParseFields(fields, 2);
                        var actual = ParamsParser.Parse(TestQuery.Parse(query), limits);

                        Assert.Equal(Number(expected, "duration_ms"), (long)actual.Duration.TotalMilliseconds);
                        Assert.Equal(Number(expected, "interval_ms"), (long)actual.Interval.TotalMilliseconds);
                        Assert.Equal((int)Number(expected, "payload_size"), actual.PayloadSize);
                        Assert.Equal(Number(expected, "expected_frames"), actual.ExpectedFrames);
                        accepted++;
                        break;
                    }

                case "err":
                    {
                        var ex = Assert.Throws<ParamException>(
                            () => ParamsParser.Parse(TestQuery.Parse(query), limits));

                        Assert.Equal(ParseFields(fields, 2)["param"], ex.Param);
                        Assert.Equal(ParseFields(fields, 2)["value"], ex.Value);
                        Assert.Equal(fields[^1]["message=".Length..], ex.Message);
                        rejected++;
                        break;
                    }

                default:
                    Assert.Fail($"unrecognised golden line: {line}");
                    break;
            }
        }

        // A canary against the fixture being truncated or regenerated with a smaller matrix.
        Assert.Equal(50, accepted + rejected);
        Assert.True(accepted > 0 && rejected > 0, "the fixture must cover both outcomes");
    }

    /// <summary>
    /// Pins the one place where the parser deliberately answers differently from the
    /// reference behaviour recorded in the golden fixture.
    /// </summary>
    /// <remarks>
    /// The fixture's original parser computed
    /// <c>time.Duration(seconds) * time.Second</c>, which wraps once the value exceeds
    /// 9223372036 seconds. Verified by running it:
    /// <c>duration=9999999999</c> yields
    /// <c>parameter "duration": must be at least 1 seconds</c> — it tells the
    /// operator an absurdly large value is too small.
    /// <para>
    /// This parser compares the bound in whole seconds, so it reports the bound that
    /// was actually violated. Both reject the request with HTTP 400. Reproducing the
    /// wrap would mean copying a bug, so these values are excluded from the golden
    /// fixture and asserted here instead.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("duration=9223372037")]
    [InlineData("duration=9999999999")]
    [InlineData("duration=9223372036854775807")]
    public void ReportsTheViolatedBoundForDurationsThatWouldOverflow(string query)
    {
        var ex = Assert.Throws<ParamException>(() => ParamsParser.Parse(TestQuery.Parse(query), Limits.Default));

        Assert.Equal("duration", ex.Param);
        Assert.Equal("must not exceed 3600 seconds", ex.Reason);
    }

    /// <summary>
    /// The same overflow exists for interval, one order of magnitude further out.
    /// </summary>
    [Theory]
    [InlineData("interval=9223372036855")]
    [InlineData("interval=9223372036854775807")]
    public void ReportsTheViolatedBoundForIntervalsThatWouldOverflow(string query)
    {
        var ex = Assert.Throws<ParamException>(() => ParamsParser.Parse(TestQuery.Parse(query), Limits.Default));

        Assert.Equal("interval", ex.Param);
        Assert.Equal("must not exceed 60000 milliseconds", ex.Reason);
    }

    /// <summary>
    /// <c>expected_frames</c> is what the console draws its progress bar from, so it
    /// has to be the ceiling, not the floor.
    /// </summary>
    [Theory]
    [InlineData(1000, 100, 10)]
    [InlineData(1000, 1000, 1)]
    [InlineData(2000, 1000, 2)]
    [InlineData(3000, 1000, 3)]
    [InlineData(3001, 1000, 4)]
    public void ExpectedFramesIsTheCeilingOfDurationOverInterval(int durationMs, int intervalMs, long expected)
    {
        var parameters = new StreamParams(
            TimeSpan.FromMilliseconds(durationMs),
            TimeSpan.FromMilliseconds(intervalMs),
            Limits.DefaultPayloadBytes);

        Assert.Equal(expected, parameters.ExpectedFrames);
    }

    private static Dictionary<string, string> ParseFields(string[] fields, int start)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < fields.Length; i++)
        {
            var separator = fields[i].IndexOf('=', StringComparison.Ordinal);
            parsed[fields[i][..separator]] = fields[i][(separator + 1)..];
        }

        return parsed;
    }

    private static long Number(Dictionary<string, string> fields, string key) =>
        long.Parse(fields[key], CultureInfo.InvariantCulture);
}
