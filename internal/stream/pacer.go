package stream

import (
	"context"
	"time"
)

// Pacer schedules frame emissions on a fixed grid anchored at the first call.
//
// It exists instead of a bare time.Ticker for two reasons:
//
//   - the grid is absolute, so a frame that is delivered slightly late does not
//     permanently shift every later frame (no drift accumulation);
//   - if the sender falls more than one interval behind (a slow client, a GC
//     pause, a saturated link) the grid is resynchronised instead of emitting a
//     catch-up burst that would distort the client's inter-arrival measurements.
type Pacer struct {
	interval time.Duration
	next     time.Time
	started  bool
	timer    *time.Timer
}

// NewPacer returns a Pacer that emits frames every interval.
func NewPacer(interval time.Duration) *Pacer {
	if interval <= 0 {
		interval = time.Millisecond
	}
	return &Pacer{interval: interval, timer: time.NewTimer(0)}
}

// Stop releases the Pacer's timer. The Pacer must not be used afterwards.
func (p *Pacer) Stop() { p.timer.Stop() }

// Wait blocks until the next frame should be emitted. It returns false when the
// deadline has already passed and the loop should finish.
//
// Wait must be called sequentially from a single goroutine.
func (p *Pacer) Wait(ctx context.Context, deadline time.Time) (bool, error) {
	now := time.Now()
	if !p.started {
		p.started = true
		p.next = now
	}

	if d := p.next.Sub(now); d > 0 {
		p.timer.Reset(d)
		select {
		case <-ctx.Done():
			return false, context.Cause(ctx)
		case <-p.timer.C:
		}
		now = time.Now()
	}

	if p.next.Sub(now) < -p.interval {
		// More than one interval behind: resynchronise rather than burst.
		p.next = now
	}

	if !now.Before(deadline) {
		return false, nil
	}

	p.next = p.next.Add(p.interval)
	return true, nil
}
