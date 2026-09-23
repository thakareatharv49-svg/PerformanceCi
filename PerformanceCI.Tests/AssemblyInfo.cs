using Xunit;

// PerfHeavy is a process-wide static event bus and PerfConsoleGate writes to Console.Out;
// running the suite serially keeps those shared surfaces deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]