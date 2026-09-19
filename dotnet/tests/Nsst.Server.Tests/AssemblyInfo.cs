using Xunit;

// PayloadDocument is process-wide mutable state, and these tests
// boot the real application which loads it. Running classes in parallel would have two hosts
// racing on that state — and on the two Kestrel-independent singletons — for no gain: the
// whole assembly finishes in seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
