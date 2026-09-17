package stream

import (
	"context"
	"errors"
	"sync"
	"sync/atomic"
	"time"

	"github.com/nsst/streamtest/internal/metrics"
)

var (
	// ErrServerShutdown is returned when a stream is requested while the server is
	// draining, and is used as the cancellation cause when the drain deadline
	// expires and remaining streams are cut.
	ErrServerShutdown = errors.New("streamtest: server is shutting down")
	// ErrTooManyStreams is returned when the concurrent stream budget is used up.
	ErrTooManyStreams = errors.New("streamtest: too many concurrent streams")
)

// Manager hands out stream leases. It enforces the concurrent stream budget and
// ties every stream lifetime to the process lifecycle so that graceful shutdown
// can wait for running tests and then forcibly release them.
type Manager struct {
	lim      Limits
	counters *metrics.Counters

	base   context.Context
	cancel context.CancelCauseFunc

	sem      chan struct{}
	active   atomic.Int64
	draining atomic.Bool
	nextID   atomic.Uint64
}

// NewManager creates a Manager for the given limits.
func NewManager(lim Limits, counters *metrics.Counters) *Manager {
	if counters == nil {
		counters = metrics.New()
	}
	base, cancel := context.WithCancelCause(context.Background())
	slots := lim.MaxConcurrentStreams
	if slots < 1 {
		slots = 1
	}
	return &Manager{
		lim:      lim,
		counters: counters,
		base:     base,
		cancel:   cancel,
		sem:      make(chan struct{}, slots),
	}
}

// Active returns the number of streams currently running.
func (m *Manager) Active() int64 { return m.active.Load() }

// Draining reports whether the server has started shutting down.
func (m *Manager) Draining() bool { return m.draining.Load() }

// StartDraining rejects new streams. Already running streams keep going until
// Drain returns or ForceClose is called.
func (m *Manager) StartDraining() { m.draining.Store(true) }

// Drain blocks until every active stream has released its lease or ctx expires.
func (m *Manager) Drain(ctx context.Context) error {
	if m.active.Load() == 0 {
		return nil
	}
	ticker := time.NewTicker(20 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
			if m.active.Load() == 0 {
				return nil
			}
		}
	}
}

// ForceClose cancels every running stream with ErrServerShutdown.
func (m *Manager) ForceClose() { m.cancel(ErrServerShutdown) }

// Lease is a granted slot to run one stream.
type Lease struct {
	ID        uint64
	StartedAt time.Time

	mgr    *Manager
	ctx    context.Context
	cancel context.CancelCauseFunc
	stop   func() bool
	once   sync.Once
	freed  atomic.Bool
}

// Context is cancelled when the client disconnects or the server shuts down.
func (l *Lease) Context() context.Context { return l.ctx }

// Release returns the lease's slot to the manager. It is safe to call more than
// once, and it is safe to call from a deferred statement.
func (l *Lease) Release() {
	if !l.freed.CompareAndSwap(false, true) {
		return
	}
	// Detach from the server base context first so the AfterFunc cannot fire and
	// cancel the lease after it has been released.
	l.stop()
	l.cancel(nil)
	l.mgr.active.Add(-1)
	l.mgr.counters.ActiveStreams.Add(-1)
	<-l.mgr.sem
}

// Acquire reserves a stream slot. The returned lease must be released by the
// caller. Acquire never blocks: when the budget is exhausted it fails fast with
// ErrTooManyStreams so the caller can answer HTTP 503.
func (m *Manager) Acquire(parent context.Context) (*Lease, error) {
	if m.draining.Load() {
		m.counters.RejectedStreams.Add(1)
		return nil, ErrServerShutdown
	}
	if parent == nil {
		parent = context.Background()
	}
	if err := parent.Err(); err != nil {
		return nil, context.Cause(parent)
	}

	select {
	case m.sem <- struct{}{}:
	default:
		m.counters.RejectedStreams.Add(1)
		return nil, ErrTooManyStreams
	}

	ctx, cancel := context.WithCancelCause(parent)
	stop := context.AfterFunc(m.base, func() { cancel(context.Cause(m.base)) })

	id := m.nextID.Add(1)
	m.active.Add(1)
	m.counters.ActiveStreams.Add(1)
	m.counters.StreamsStarted.Add(1)

	return &Lease{
		ID:        id,
		StartedAt: time.Now(),
		mgr:       m,
		ctx:       ctx,
		cancel:    cancel,
		stop:      stop,
	}, nil
}

// Record increments the completion counters for a finished stream.
func (m *Manager) Record(res Result) {
	switch res.Reason {
	case EndCompleted:
		m.counters.StreamsFinished.Add(1)
	case EndWriteError:
		m.counters.ErroredStreams.Add(1)
	default:
		m.counters.CancelledStreams.Add(1)
	}
}

// Snapshot returns a point in time copy of the server wide counters.
func (m *Manager) Snapshot() metrics.Snapshot { return m.counters.Snapshot() }
