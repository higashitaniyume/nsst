package stream

import (
	"context"
	"errors"
	"testing"
	"time"
)

func TestPacerEmitsImmediatelyThenOnGrid(t *testing.T) {
	const interval = 20 * time.Millisecond
	const duration = 200 * time.Millisecond

	pacer := NewPacer(interval)
	defer pacer.Stop()

	start := time.Now()
	deadline := start.Add(duration)

	var stamps []time.Duration
	for {
		emit, err := pacer.Wait(context.Background(), deadline)
		if err != nil {
			t.Fatalf("Wait: %v", err)
		}
		if !emit {
			break
		}
		stamps = append(stamps, time.Since(start))
	}

	if want := int(duration / interval); len(stamps) != want {
		t.Fatalf("emitted %d frames, want %d (%v)", len(stamps), want, stamps)
	}
	if stamps[0] > interval/2 {
		t.Fatalf("first frame was delayed by %v, want an immediate first emission", stamps[0])
	}
	for i := 1; i < len(stamps); i++ {
		gap := stamps[i] - stamps[i-1]
		if gap < interval/2 || gap > interval*4 {
			t.Errorf("gap %d between frames %d and %d was %v, expected about %v", i, i, i+1, gap, interval)
		}
	}
}

func TestPacerStopsAtDeadline(t *testing.T) {
	pacer := NewPacer(time.Millisecond)
	defer pacer.Stop()

	// A deadline in the past still allows the immediate first emission to be
	// suppressed: nothing may be sent after the deadline.
	deadline := time.Now().Add(-time.Second)
	emit, err := pacer.Wait(context.Background(), deadline)
	if err != nil {
		t.Fatalf("Wait: %v", err)
	}
	if emit {
		t.Fatal("Pacer emitted past its deadline")
	}
}

func TestPacerHonoursContextCancellation(t *testing.T) {
	pacer := NewPacer(time.Hour)
	defer pacer.Stop()

	// Consume the immediate first tick so the next Wait blocks on the timer.
	if emit, err := pacer.Wait(context.Background(), time.Now().Add(time.Hour)); err != nil || !emit {
		t.Fatalf("first Wait: emit=%v err=%v", emit, err)
	}

	ctx, cancel := context.WithCancel(context.Background())
	go func() {
		time.Sleep(20 * time.Millisecond)
		cancel()
	}()

	start := time.Now()
	emit, err := pacer.Wait(ctx, time.Now().Add(time.Hour))
	if emit {
		t.Fatal("Wait reported an emission after cancellation")
	}
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("err = %v, want context.Canceled", err)
	}
	if elapsed := time.Since(start); elapsed > time.Second {
		t.Fatalf("Wait blocked for %v after cancellation", elapsed)
	}
}

func TestPacerResynchronisesAfterLongStall(t *testing.T) {
	pacer := NewPacer(10 * time.Millisecond)
	defer pacer.Stop()

	// First Wait emits immediately.
	if emit, err := pacer.Wait(context.Background(), time.Now().Add(time.Hour)); err != nil || !emit {
		t.Fatalf("first Wait: emit=%v err=%v", emit, err)
	}

	// Simulate a stalled sender: the next Wait is called well after several
	// intervals have elapsed. It must not emit a catch-up burst.
	time.Sleep(60 * time.Millisecond)

	start := time.Now()
	emit, err := pacer.Wait(context.Background(), time.Now().Add(time.Hour))
	if err != nil || !emit {
		t.Fatalf("second Wait: emit=%v err=%v", emit, err)
	}
	if elapsed := time.Since(start); elapsed > 5*time.Millisecond {
		t.Fatalf("stalled pacer waited %v; it should resynchronise and emit immediately", elapsed)
	}

	// The following emission must be one fresh interval away, not immediate.
	start = time.Now()
	if emit, err = pacer.Wait(context.Background(), time.Now().Add(time.Hour)); err != nil || !emit {
		t.Fatalf("third Wait: emit=%v err=%v", emit, err)
	}
	if elapsed := time.Since(start); elapsed < 5*time.Millisecond {
		t.Fatalf("pacer did not resynchronise: next frame came after only %v", elapsed)
	}
}
