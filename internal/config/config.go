// Package config loads server configuration from environment variables and turns
// it into the parameter limits enforced by the streaming handlers.
package config

import (
	"fmt"
	"log/slog"
	"net"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/nsst/streamtest/internal/stream"
)

// Name is the service name reported by /api/info.
const Name = "StreamTest"

// Version is reported by /api/info, /api/health and /api/config.
//
// It is a variable rather than a constant so that release builds can stamp a
// real version in at link time:
//
//	go build -ldflags "-X github.com/nsst/streamtest/internal/config.Version=1.2.3"
//
// Release builds always overwrite it; the value below is what a plain
// `go build` reports, and it tracks the most recent release.
var Version = "0.2.0"

// Config is the fully resolved server configuration.
type Config struct {
	Host             string
	Port             int
	CORSAllowOrigins []string
	LogLevel         slog.Level
	LogFormat        string
	ShutdownTimeout  time.Duration
	Limits           stream.Limits

	// PayloadFile is the path of a file to use as the streaming payload
	// document instead of the embedded one. Empty means "use the embedded
	// document"; cmd/server turns it into a call to protocol.LoadDocument.
	PayloadFile string
}

// Addr is the listen address for net/http.
func (c Config) Addr() string { return net.JoinHostPort(c.Host, strconv.Itoa(c.Port)) }

// Load reads configuration from the process environment.
func Load() (Config, error) { return LoadFrom(os.Getenv) }

// LoadFrom reads configuration through getenv. It is exported so tests can
// exercise parsing without mutating the process environment.
//
// Every setting has a safe default; an unparsable or out of range value is
// reported as an error rather than silently ignored.
func LoadFrom(getenv func(string) string) (Config, error) {
	l := &loader{getenv: getenv}

	cfg := Config{
		Host:             l.str("HOST", ""),
		Port:             l.intVal("PORT", 8080, 1, 65535),
		CORSAllowOrigins: splitList(l.str("CORS_ALLOW_ORIGINS", "")),
		LogLevel:         l.logLevel("LOG_LEVEL", slog.LevelInfo),
		LogFormat:        l.enum("LOG_FORMAT", "text", "text", "json"),
		ShutdownTimeout:  time.Duration(l.intVal("SHUTDOWN_TIMEOUT", 15, 1, 600)) * time.Second,
		PayloadFile:      l.str("PAYLOAD_FILE", ""),
	}

	lim := stream.DefaultLimits()
	lim.MaxDuration = time.Duration(l.intVal("MAX_DURATION", 3600, 1, 86400)) * time.Second
	lim.MaxPayloadSize = l.intVal("MAX_PAYLOAD_SIZE", 1<<20, 0, 64<<20)
	lim.MinInterval = time.Duration(l.intVal("MIN_INTERVAL", 10, 1, 60000)) * time.Millisecond
	lim.MaxInterval = time.Duration(l.intVal("MAX_INTERVAL", 60000, 1, 3600000)) * time.Millisecond
	lim.MaxConcurrentStreams = l.intVal("MAX_CONCURRENT_STREAMS", 100, 1, 100000)
	lim.WriteTimeout = time.Duration(l.intVal("WRITE_TIMEOUT_MS", 15000, 100, 600000)) * time.Millisecond

	if len(l.errs) > 0 {
		return Config{}, fmt.Errorf("invalid configuration: %s", strings.Join(l.errs, "; "))
	}

	if lim.MaxInterval < lim.MinInterval {
		return Config{}, fmt.Errorf("invalid configuration: MAX_INTERVAL (%s) must be >= MIN_INTERVAL (%s)",
			lim.MaxInterval, lim.MinInterval)
	}

	// Keep the defaults inside the configured bounds so the advertised defaults
	// are always legal requests.
	lim.DefaultDuration = clamp(lim.DefaultDuration, lim.MinDuration, lim.MaxDuration)
	lim.DefaultInterval = clamp(lim.DefaultInterval, lim.MinInterval, lim.MaxInterval)
	if lim.DefaultPayloadSize > lim.MaxPayloadSize {
		lim.DefaultPayloadSize = lim.MaxPayloadSize
	}

	cfg.Limits = lim
	return cfg, nil
}

func clamp(v, lo, hi time.Duration) time.Duration {
	if v < lo {
		return lo
	}
	if v > hi {
		return hi
	}
	return v
}

type loader struct {
	getenv func(string) string
	errs   []string
}

func (l *loader) str(key, def string) string {
	v := strings.TrimSpace(l.getenv(key))
	if v == "" {
		return def
	}
	return v
}

func (l *loader) intVal(key string, def, min, max int) int {
	raw := strings.TrimSpace(l.getenv(key))
	if raw == "" {
		return def
	}
	v, err := strconv.Atoi(raw)
	if err != nil {
		l.errs = append(l.errs, fmt.Sprintf("%s=%q is not an integer", key, raw))
		return def
	}
	if v < min || v > max {
		l.errs = append(l.errs, fmt.Sprintf("%s=%d is outside the allowed range [%d, %d]", key, v, min, max))
		return def
	}
	return v
}

func (l *loader) enum(key, def string, allowed ...string) string {
	v := strings.ToLower(l.str(key, def))
	for _, a := range allowed {
		if v == a {
			return v
		}
	}
	l.errs = append(l.errs, fmt.Sprintf("%s=%q must be one of %s", key, v, strings.Join(allowed, ", ")))
	return def
}

func (l *loader) logLevel(key string, def slog.Level) slog.Level {
	raw := l.str(key, "")
	if raw == "" {
		return def
	}
	switch strings.ToLower(raw) {
	case "debug":
		return slog.LevelDebug
	case "info":
		return slog.LevelInfo
	case "warn", "warning":
		return slog.LevelWarn
	case "error":
		return slog.LevelError
	default:
		l.errs = append(l.errs, fmt.Sprintf("%s=%q must be one of debug, info, warn, error", key, raw))
		return def
	}
}

func splitList(v string) []string {
	if strings.TrimSpace(v) == "" {
		return nil
	}
	parts := strings.Split(v, ",")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		if p = strings.TrimSpace(p); p != "" {
			out = append(out, p)
		}
	}
	if len(out) == 0 {
		return nil
	}
	return out
}
