using System.Globalization;
using System.Reflection;
using Nsst.Core.Streaming;

namespace Nsst.Server;

/// <summary>
/// Server configuration, read from the process environment.
/// </summary>
/// <remarks>
/// The variable names, defaults and accepted ranges are the deployment contract: the
/// Compose file and the README document them, so an existing deployment keeps working
/// without a configuration change. Every setting has a safe default; an unparsable or
/// out-of-range value is an error rather than being silently ignored, so a typo in a
/// deployment fails loudly at startup.
/// </remarks>
public sealed record ServerConfig
{
    /// <summary>The service name reported by /api/info.</summary>
    public const string ServiceName = "StreamTest";

    /// <summary>
    /// The version reported by /api/info, /api/health and /api/config.
    /// </summary>
    /// <remarks>
    /// Read from the assembly's informational version, which MSBuild stamps from the
    /// <c>Version</c> property in Directory.Build.props. A release pipeline therefore sets the
    /// reported version with <c>-p:Version=</c> — as the Dockerfile does — instead of editing
    /// source, and there is no second copy of the number in C# that could disagree with it.
    ///
    /// The SDK appends the source revision as <c>+&lt;sha&gt;</c> when it builds inside a git
    /// checkout. That is build metadata, not part of the release number, so it is trimmed.
    ///
    /// A missing attribute yields "unknown" rather than an exception: a wrong version string
    /// is not a reason to refuse to start and stop serving streams.
    /// </remarks>
    public static string ServiceVersion { get; } = ReadServiceVersion();

    private static string ReadServiceVersion()
    {
        var informational = typeof(ServerConfig).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return "unknown";
        }

        // Trim the SDK's "+<source revision>" build metadata, keeping a leading one intact
        // (IndexOf > 0 rather than >= 0) in case a version ever legitimately starts with '+'.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }

    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>Empty means no cross-origin access is allowed at all.</summary>
    public required IReadOnlyList<string> CorsAllowOrigins { get; init; }

    public required LogLevel LogLevel { get; init; }

    /// <summary>Either <c>text</c> or <c>json</c>.</summary>
    public required string LogFormat { get; init; }

    public required TimeSpan ShutdownTimeout { get; init; }

    public required Limits Limits { get; init; }

    /// <summary>
    /// A file to use as the streaming payload document instead of the embedded one.
    /// </summary>
    /// <remarks>
    /// Empty means "use the embedded document". <see cref="Program"/> turns this into a
    /// call to <c>PayloadDocument.LoadDocument</c>. Nullable rather than empty-string
    /// because <c>PayloadDocument.LoadDocument</c> treats null and blank alike.
    /// </remarks>
    public string? PayloadFile { get; init; }

    /// <summary>The listen address as <c>host:port</c>, with an IPv6 host bracketed.</summary>
    public string Address => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>Reads <see cref="ServerConfig"/> from an environment lookup.</summary>
public static class ServerConfigLoader
{
    /// <summary>Reads configuration from the process environment.</summary>
    public static ServerConfig Load() => LoadFrom(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Reads configuration through <paramref name="getenv"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so tests can exercise parsing without mutating the process environment.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A value was unparsable or out of range.</exception>
    public static ServerConfig LoadFrom(Func<string, string?> getenv)
    {
        ArgumentNullException.ThrowIfNull(getenv);

        var errors = new List<string>();

        string Str(string key, string fallback)
        {
            var value = getenv(key)?.Trim();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        int IntVal(string key, int fallback, int min, int max)
        {
            var raw = getenv(key)?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return fallback;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                errors.Add($"{key}=\"{raw}\" is not an integer");
                return fallback;
            }

            if (value < min || value > max)
            {
                errors.Add($"{key}={value} is outside the allowed range [{min}, {max}]");
                return fallback;
            }

            return value;
        }

        string Enum(string key, string fallback, params string[] allowed)
        {
            var value = Str(key, fallback).ToLowerInvariant();
            if (Array.IndexOf(allowed, value) >= 0)
            {
                return value;
            }

            errors.Add($"{key}=\"{value}\" must be one of {string.Join(", ", allowed)}");
            return fallback;
        }

        LogLevel LogLevelVal(string key, LogLevel fallback)
        {
            var raw = Str(key, string.Empty);
            switch (raw.ToLowerInvariant())
            {
                case "":
                    return fallback;
                case "debug":
                    return LogLevel.Debug;
                case "info":
                    return LogLevel.Information;
                case "warn" or "warning":
                    return LogLevel.Warning;
                case "error":
                    return LogLevel.Error;
                default:
                    errors.Add($"{key}=\"{raw}\" must be one of debug, info, warn, error");
                    return fallback;
            }
        }

        var config = new ServerConfig
        {
            Host = Str("HOST", string.Empty),
            Port = IntVal("PORT", 8080, 1, 65535),
            CorsAllowOrigins = SplitList(Str("CORS_ALLOW_ORIGINS", string.Empty)),
            LogLevel = LogLevelVal("LOG_LEVEL", LogLevel.Information),
            LogFormat = Enum("LOG_FORMAT", "text", "text", "json"),
            ShutdownTimeout = TimeSpan.FromSeconds(IntVal("SHUTDOWN_TIMEOUT", 15, 1, 600)),
            PayloadFile = Str("PAYLOAD_FILE", string.Empty),
            Limits = default!,
        };

        var limits = Limits.Default with
        {
            MaxDuration = TimeSpan.FromSeconds(IntVal("MAX_DURATION", 3600, 1, 86400)),
            MaxPayloadSize = IntVal("MAX_PAYLOAD_SIZE", 1 << 20, 0, 64 << 20),
            MinInterval = TimeSpan.FromMilliseconds(IntVal("MIN_INTERVAL", 10, 1, 60000)),
            MaxInterval = TimeSpan.FromMilliseconds(IntVal("MAX_INTERVAL", 60000, 1, 3600000)),
            MaxConcurrentStreams = IntVal("MAX_CONCURRENT_STREAMS", 100, 1, 100000),
            WriteTimeout = TimeSpan.FromMilliseconds(IntVal("WRITE_TIMEOUT_MS", 15000, 100, 600000)),
        };

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"invalid configuration: {string.Join("; ", errors)}");
        }

        if (limits.MaxInterval < limits.MinInterval)
        {
            throw new InvalidOperationException(
                $"invalid configuration: MAX_INTERVAL ({limits.MaxInterval}) must be >= MIN_INTERVAL ({limits.MinInterval})");
        }

        // Keep the defaults inside the configured bounds so the defaults advertised by
        // /api/config are always legal requests.
        limits = limits with
        {
            DefaultDuration = Clamp(limits.DefaultDuration, limits.MinDuration, limits.MaxDuration),
            DefaultInterval = Clamp(limits.DefaultInterval, limits.MinInterval, limits.MaxInterval),
            DefaultPayloadSize = Math.Min(limits.DefaultPayloadSize, limits.MaxPayloadSize),
        };

        return config with { Limits = limits };
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) =>
        value < low ? low : value > high ? high : value;

    private static IReadOnlyList<string> SplitList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var parts = new List<string>();
        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                parts.Add(trimmed);
            }
        }

        return parts;
    }
}
