using Xunit;

// PayloadDocument is a single process-wide document: it is swapped wholesale by
// LoadDocument. Tests therefore cannot run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
