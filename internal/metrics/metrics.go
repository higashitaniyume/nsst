// Package metrics holds the server side counters exposed by /api/metrics.
//
// The counters are intentionally tiny and lock free: the process serves long
// lived streaming connections and the hot path must not contend on a mutex.
package metrics

import "sync/atomic"

// Counters aggregates server wide stream statistics.
type Counters struct {
	ActiveStreams    atomic.Int64
	StreamsStarted   atomic.Uint64
	StreamsFinished  atomic.Uint64
	CancelledStreams atomic.Uint64
	ErroredStreams   atomic.Uint64
	RejectedStreams  atomic.Uint64
	FramesSent       atomic.Uint64
	BytesSent        atomic.Uint64
}

// New returns a ready to use Counters.
func New() *Counters { return &Counters{} }

// Snapshot is a point in time copy of the counters, safe to serialise.
type Snapshot struct {
	ActiveStreams    int64  `json:"active_streams"`
	StreamsStarted   uint64 `json:"streams_started"`
	StreamsFinished  uint64 `json:"streams_finished"`
	CancelledStreams uint64 `json:"streams_cancelled"`
	ErroredStreams   uint64 `json:"streams_errored"`
	RejectedStreams  uint64 `json:"streams_rejected"`
	FramesSent       uint64 `json:"frames_sent"`
	BytesSent        uint64 `json:"bytes_sent"`
}

// Snapshot reads all counters with relaxed atomic loads.
func (c *Counters) Snapshot() Snapshot {
	return Snapshot{
		ActiveStreams:    c.ActiveStreams.Load(),
		StreamsStarted:   c.StreamsStarted.Load(),
		StreamsFinished:  c.StreamsFinished.Load(),
		CancelledStreams: c.CancelledStreams.Load(),
		ErroredStreams:   c.ErroredStreams.Load(),
		RejectedStreams:  c.RejectedStreams.Load(),
		FramesSent:       c.FramesSent.Load(),
		BytesSent:        c.BytesSent.Load(),
	}
}
