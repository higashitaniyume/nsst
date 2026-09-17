package config

import (
	"log/slog"
	"strings"
	"testing"
	"time"
)

// env builds a getenv function from a map.
func env(values map[string]string) func(string) string {
	return func(key string) string { return values[key] }
}

func TestLoadFromDefaults(t *testing.T) {
	cfg, err := LoadFrom(env(nil))
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}

	if cfg.Port != 8080 {
		t.Errorf("port = %d, want 8080", cfg.Port)
	}
	if cfg.Addr() != ":8080" {
		t.Errorf("addr = %q, want \":8080\"", cfg.Addr())
	}
	if cfg.ShutdownTimeout != 15*time.Second {
		t.Errorf("shutdown timeout = %v, want 15s", cfg.ShutdownTimeout)
	}
	if cfg.LogLevel != slog.LevelInfo {
		t.Errorf("log level = %v, want info", cfg.LogLevel)
	}
	if cfg.LogFormat != "text" {
		t.Errorf("log format = %q, want text", cfg.LogFormat)
	}
	if len(cfg.CORSAllowOrigins) != 0 {
		t.Errorf("cors origins = %v, want none", cfg.CORSAllowOrigins)
	}

	lim := cfg.Limits
	if lim.MaxDuration != time.Hour {
		t.Errorf("max duration = %v, want 1h", lim.MaxDuration)
	}
	if lim.MinInterval != 10*time.Millisecond {
		t.Errorf("min interval = %v, want 10ms", lim.MinInterval)
	}
	if lim.MaxPayloadSize != 1<<20 {
		t.Errorf("max payload = %d, want 1MiB", lim.MaxPayloadSize)
	}
	if lim.MaxConcurrentStreams != 100 {
		t.Errorf("max concurrent streams = %d, want 100", lim.MaxConcurrentStreams)
	}
	if lim.WriteTimeout != 15*time.Second {
		t.Errorf("write timeout = %v, want 15s", lim.WriteTimeout)
	}
	if lim.DefaultDuration != time.Minute || lim.DefaultInterval != 100*time.Millisecond || lim.DefaultPayloadSize != 4096 {
		t.Errorf("unexpected defaults: %+v", lim)
	}
}

func TestLoadFromOverrides(t *testing.T) {
	cfg, err := LoadFrom(env(map[string]string{
		"HOST":                   "127.0.0.1",
		"PORT":                   "9090",
		"MAX_DURATION":           "120",
		"MAX_PAYLOAD_SIZE":       "65536",
		"MIN_INTERVAL":           "25",
		"MAX_INTERVAL":           "5000",
		"MAX_CONCURRENT_STREAMS": "7",
		"WRITE_TIMEOUT_MS":       "2500",
		"SHUTDOWN_TIMEOUT":       "3",
		"CORS_ALLOW_ORIGINS":     "https://example.com, http://localhost:5173",
		"LOG_LEVEL":              "debug",
		"LOG_FORMAT":             "json",
	}))
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}

	if cfg.Addr() != "127.0.0.1:9090" {
		t.Errorf("addr = %q", cfg.Addr())
	}
	if cfg.Limits.MaxDuration != 2*time.Minute {
		t.Errorf("max duration = %v", cfg.Limits.MaxDuration)
	}
	if cfg.Limits.MaxPayloadSize != 65536 {
		t.Errorf("max payload = %d", cfg.Limits.MaxPayloadSize)
	}
	if cfg.Limits.MinInterval != 25*time.Millisecond {
		t.Errorf("min interval = %v", cfg.Limits.MinInterval)
	}
	if cfg.Limits.MaxInterval != 5*time.Second {
		t.Errorf("max interval = %v", cfg.Limits.MaxInterval)
	}
	if cfg.Limits.MaxConcurrentStreams != 7 {
		t.Errorf("max concurrent streams = %d", cfg.Limits.MaxConcurrentStreams)
	}
	if cfg.Limits.WriteTimeout != 2500*time.Millisecond {
		t.Errorf("write timeout = %v", cfg.Limits.WriteTimeout)
	}
	if cfg.ShutdownTimeout != 3*time.Second {
		t.Errorf("shutdown timeout = %v", cfg.ShutdownTimeout)
	}
	if len(cfg.CORSAllowOrigins) != 2 || cfg.CORSAllowOrigins[0] != "https://example.com" || cfg.CORSAllowOrigins[1] != "http://localhost:5173" {
		t.Errorf("cors origins = %v", cfg.CORSAllowOrigins)
	}
	if cfg.LogLevel != slog.LevelDebug {
		t.Errorf("log level = %v", cfg.LogLevel)
	}
	if cfg.LogFormat != "json" {
		t.Errorf("log format = %q", cfg.LogFormat)
	}
}

func TestLoadFromRejectsInvalidValues(t *testing.T) {
	tests := []struct {
		name    string
		values  map[string]string
		mention string
	}{
		{"port not a number", map[string]string{"PORT": "abc"}, "PORT"},
		{"port out of range", map[string]string{"PORT": "70000"}, "PORT"},
		{"max duration not a number", map[string]string{"MAX_DURATION": "soon"}, "MAX_DURATION"},
		{"max duration zero", map[string]string{"MAX_DURATION": "0"}, "MAX_DURATION"},
		{"max payload negative", map[string]string{"MAX_PAYLOAD_SIZE": "-1"}, "MAX_PAYLOAD_SIZE"},
		{"min interval zero", map[string]string{"MIN_INTERVAL": "0"}, "MIN_INTERVAL"},
		{"max concurrent streams zero", map[string]string{"MAX_CONCURRENT_STREAMS": "0"}, "MAX_CONCURRENT_STREAMS"},
		{"write timeout too small", map[string]string{"WRITE_TIMEOUT_MS": "1"}, "WRITE_TIMEOUT_MS"},
		{"bad log level", map[string]string{"LOG_LEVEL": "chatty"}, "LOG_LEVEL"},
		{"bad log format", map[string]string{"LOG_FORMAT": "xml"}, "LOG_FORMAT"},
		{"max interval below min interval", map[string]string{"MIN_INTERVAL": "1000", "MAX_INTERVAL": "10"}, "MAX_INTERVAL"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			_, err := LoadFrom(env(tc.values))
			if err == nil {
				t.Fatal("expected an error")
			}
			if !strings.Contains(err.Error(), tc.mention) {
				t.Fatalf("error %q does not mention %s", err, tc.mention)
			}
		})
	}
}

func TestLoadFromClampsDefaultsIntoBounds(t *testing.T) {
	// A deployment may configure limits tighter than the built in defaults; the
	// advertised defaults must stay legal.
	cfg, err := LoadFrom(env(map[string]string{
		"MAX_DURATION":     "10",
		"MIN_INTERVAL":     "500",
		"MAX_INTERVAL":     "1000",
		"MAX_PAYLOAD_SIZE": "512",
	}))
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}

	if cfg.Limits.DefaultDuration > cfg.Limits.MaxDuration {
		t.Errorf("default duration %v exceeds max %v", cfg.Limits.DefaultDuration, cfg.Limits.MaxDuration)
	}
	if cfg.Limits.DefaultInterval < cfg.Limits.MinInterval || cfg.Limits.DefaultInterval > cfg.Limits.MaxInterval {
		t.Errorf("default interval %v outside [%v, %v]", cfg.Limits.DefaultInterval, cfg.Limits.MinInterval, cfg.Limits.MaxInterval)
	}
	if cfg.Limits.DefaultPayloadSize > cfg.Limits.MaxPayloadSize {
		t.Errorf("default payload %d exceeds max %d", cfg.Limits.DefaultPayloadSize, cfg.Limits.MaxPayloadSize)
	}
}

func TestLoadFromBlankValuesUseDefaults(t *testing.T) {
	cfg, err := LoadFrom(env(map[string]string{"PORT": "  ", "LOG_LEVEL": ""}))
	if err != nil {
		t.Fatalf("LoadFrom: %v", err)
	}
	if cfg.Port != 8080 {
		t.Errorf("port = %d, want 8080", cfg.Port)
	}
}
