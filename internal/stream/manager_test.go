package stream

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/nsst/streamtest/internal/metrics"
)

func TestManagerEnforcesConcurrentStreamLimit(t *testing.T) {
	lim := DefaultLimits()
	lim.MaxConcurrentStreams = 2
	mgr := NewManager(lim, metrics.New())

	leases := make([]*Lease, 0, 2)
	for i := 0; i < 2; i++ {
		lease, err := mgr.Acquire(context.Background())
		if err != nil {
			t.Fatalf("Acquire %d: %v", i, err)
		}
		leases = append(leases, lease)
	}
	if got := mgr.Active(); got != 2 {
		t.Fatalf("active = %d, want 2", got)
	}

	if _, err := mgr.Acquire(context.Background()); !errors.Is(err, ErrTooManyStreams) {
		t.Fatalf("third Acquire error = %v, want ErrTooManyStreams", err)
	}

	leases[0].Release()
	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire after release: %v", err)
	}
	lease.Release()
	leases[1].Release()

	if got := mgr.Active(); got != 0 {
		t.Fatalf("active = %d, want 0", got)
	}
}

func TestManagerReleasesSlotExactlyOnce(t *testing.T) {
	lim := DefaultLimits()
	lim.MaxConcurrentStreams = 1
	mgr := NewManager(lim, metrics.New())

	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}

	var wg sync.WaitGroup
	for i := 0; i < 16; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			lease.Release()
		}()
	}
	wg.Wait()

	if got := mgr.Active(); got != 0 {
		t.Fatalf("active = %d, want 0 after concurrent releases", got)
	}
	// The slot must be reusable: a double release would have drained it twice.
	second, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire after concurrent releases: %v", err)
	}
	second.Release()
}

func TestManagerRejectsNewStreamsWhileDraining(t *testing.T) {
	mgr := NewManager(DefaultLimits(), metrics.New())

	first, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}
	defer first.Release()

	mgr.StartDraining()
	if !mgr.Draining() {
		t.Fatal("Draining() = false after StartDraining")
	}
	if _, err := mgr.Acquire(context.Background()); !errors.Is(err, ErrServerShutdown) {
		t.Fatalf("Acquire while draining = %v, want ErrServerShutdown", err)
	}
}

func TestManagerDrainWaitsForRelease(t *testing.T) {
	mgr := NewManager(DefaultLimits(), metrics.New())
	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}

	released := make(chan struct{})
	go func() {
		time.Sleep(80 * time.Millisecond)
		lease.Release()
		close(released)
	}()

	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	if err := mgr.Drain(ctx); err != nil {
		t.Fatalf("Drain: %v", err)
	}
	<-released
	if got := mgr.Active(); got != 0 {
		t.Fatalf("active = %d, want 0", got)
	}
}

func TestManagerDrainRespectsDeadline(t *testing.T) {
	mgr := NewManager(DefaultLimits(), metrics.New())
	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}
	defer lease.Release()

	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	if err := mgr.Drain(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("Drain error = %v, want context.DeadlineExceeded", err)
	}
}

func TestManagerForceCloseCancelsLeases(t *testing.T) {
	mgr := NewManager(DefaultLimits(), metrics.New())
	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}
	defer lease.Release()

	mgr.ForceClose()

	select {
	case <-lease.Context().Done():
	case <-time.After(time.Second):
		t.Fatal("lease context was not cancelled by ForceClose")
	}
	if cause := context.Cause(lease.Context()); !errors.Is(cause, ErrServerShutdown) {
		t.Fatalf("cause = %v, want ErrServerShutdown", cause)
	}
}

func TestManagerAcquireRejectsCancelledParent(t *testing.T) {
	mgr := NewManager(DefaultLimits(), metrics.New())

	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := mgr.Acquire(ctx); err == nil {
		t.Fatal("Acquire with an already cancelled parent must fail")
	}
	if got := mgr.Active(); got != 0 {
		t.Fatalf("active = %d, want 0", got)
	}

	// The rejected attempt must not have consumed a slot.
	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire after a rejected parent: %v", err)
	}
	lease.Release()
}

func TestManagerCountsRejections(t *testing.T) {
	lim := DefaultLimits()
	lim.MaxConcurrentStreams = 1
	counters := metrics.New()
	mgr := NewManager(lim, counters)

	lease, err := mgr.Acquire(context.Background())
	if err != nil {
		t.Fatalf("Acquire: %v", err)
	}
	defer lease.Release()

	if _, err := mgr.Acquire(context.Background()); err == nil {
		t.Fatal("expected rejection")
	}
	if got := counters.RejectedStreams.Load(); got != 1 {
		t.Fatalf("rejected_streams = %d, want 1", got)
	}
	if got := counters.ActiveStreams.Load(); got != 1 {
		t.Fatalf("active_streams = %d, want 1", got)
	}
}
