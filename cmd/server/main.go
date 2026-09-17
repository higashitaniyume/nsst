// Command server runs the Network Stream Stability Tester: a single process that
// serves the web UI, the REST API, and the HTTP streaming, SSE and WebSocket
// stream endpoints.
package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/nsst/streamtest/internal/api"
	"github.com/nsst/streamtest/internal/config"
	"github.com/nsst/streamtest/internal/httpx"
	"github.com/nsst/streamtest/internal/metrics"
	"github.com/nsst/streamtest/internal/stream"
	"github.com/nsst/streamtest/internal/webui"
)

// closeTimeout bounds the final socket teardown after streams have been drained.
const closeTimeout = 5 * time.Second

func main() {
	if err := run(); err != nil {
		// The logger may not exist yet, so report through slog's default handler.
		slog.Error("fatal", "error", err)
		os.Exit(1)
	}
}

func run() error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}
	logger := newLogger(cfg)
	slog.SetDefault(logger)

	counters := metrics.New()
	manager := stream.NewManager(cfg.Limits, counters)

	static, err := webui.Handler()
	if err != nil {
		return fmt.Errorf("embedded front end: %w", err)
	}

	handler := api.New(api.Options{
		Version:  config.Version,
		Name:     config.Name,
		Limits:   cfg.Limits,
		Manager:  manager,
		Counters: counters,
		Logger:   logger,
		CORS:     httpx.NewCORS(cfg.CORSAllowOrigins),
		Static:   static,
	})

	srv := &http.Server{
		Handler: handler,
		// ReadTimeout and WriteTimeout are intentionally unset: both would cap the
		// lifetime of a streaming response. Requests have no bodies, and per frame
		// write deadlines are applied with http.ResponseController instead.
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       120 * time.Second,
		MaxHeaderBytes:    32 << 10,
		ErrorLog:          slog.NewLogLogger(logger.Handler(), slog.LevelWarn),
	}

	ln, err := net.Listen("tcp", cfg.Addr())
	if err != nil {
		return fmt.Errorf("listen on %s: %w", cfg.Addr(), err)
	}

	logger.Info("server start",
		"addr", ln.Addr().String(),
		"version", config.Version,
		"max_duration", cfg.Limits.MaxDuration.String(),
		"min_interval", cfg.Limits.MinInterval.String(),
		"max_payload_size", cfg.Limits.MaxPayloadSize,
		"max_concurrent_streams", cfg.Limits.MaxConcurrentStreams,
		"write_timeout", cfg.Limits.WriteTimeout.String(),
		"shutdown_timeout", cfg.ShutdownTimeout.String(),
		"cors_allow_origins", cfg.CORSAllowOrigins,
	)

	serveErr := make(chan error, 1)
	go func() {
		if err := srv.Serve(ln); err != nil && !errors.Is(err, http.ErrServerClosed) {
			serveErr <- err
			return
		}
		serveErr <- nil
	}()

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	select {
	case err := <-serveErr:
		if err != nil {
			return fmt.Errorf("http server: %w", err)
		}
		return nil
	case <-ctx.Done():
		logger.Info("server shutdown requested", "signal", ctx.Err().Error())
	}

	shutdownServer(srv, manager, logger, cfg)
	return nil
}

// shutdownServer implements the graceful shutdown sequence:
//
//  1. stop accepting new streams (the manager rejects them with 503);
//  2. stop accepting new connections and let net/http unwind idle keep-alives;
//  3. wait up to SHUTDOWN_TIMEOUT for in-flight streams to finish on their own;
//  4. cancel whatever is left (which closes WebSockets with a going-away frame
//     and ends NDJSON/SSE responses) and give the sockets a short final budget.
func shutdownServer(srv *http.Server, mgr *stream.Manager, logger *slog.Logger, cfg config.Config) {
	mgr.StartDraining()
	logger.Info("shutdown: draining",
		"active_streams", mgr.Active(),
		"timeout", cfg.ShutdownTimeout.String(),
	)

	shutdownDone := make(chan error, 1)
	go func() { shutdownDone <- srv.Shutdown(context.Background()) }()

	drainCtx, cancelDrain := context.WithTimeout(context.Background(), cfg.ShutdownTimeout)
	drainErr := mgr.Drain(drainCtx)
	cancelDrain()

	switch {
	case drainErr == nil:
		logger.Info("shutdown: streams drained")
	case errors.Is(drainErr, context.DeadlineExceeded):
		logger.Warn("shutdown: drain timeout reached, cancelling remaining streams",
			"active_streams", mgr.Active())
	default:
		logger.Info("shutdown: drain stopped", "reason", drainErr.Error(), "active_streams", mgr.Active())
	}
	// Cancelling is a no-op when everything has already finished.
	mgr.ForceClose()

	select {
	case err := <-shutdownDone:
		if err != nil {
			logger.Warn("shutdown: http server stopped with error", "error", err)
		}
	case <-time.After(closeTimeout):
		logger.Warn("shutdown: forcing remaining connections closed")
		if err := srv.Close(); err != nil {
			logger.Warn("shutdown: close failed", "error", err)
		}
		<-shutdownDone
	}

	snap := mgr.Snapshot()
	logger.Info("server shutdown complete",
		"streams_started", snap.StreamsStarted,
		"streams_finished", snap.StreamsFinished,
		"frames_sent", snap.FramesSent,
		"bytes_sent", snap.BytesSent,
	)
}

func newLogger(cfg config.Config) *slog.Logger {
	opts := &slog.HandlerOptions{Level: cfg.LogLevel}
	var handler slog.Handler
	if cfg.LogFormat == "json" {
		handler = slog.NewJSONHandler(os.Stdout, opts)
	} else {
		handler = slog.NewTextHandler(os.Stdout, opts)
	}
	return slog.New(handler)
}
