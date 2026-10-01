namespace ForgeMission.Tests.Cli;

// Tests that keep every core busy for many seconds (CardEdgeTests renders 802 tile sets across
// all thread-pool threads) starve timing-sensitive tests elsewhere in the suite, such as a test
// that expects two 60 ms steps to overlap. This collection runs on its own, after the parallel ones.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuBoundCollection
{
    public const string Name = "CPU-bound";
}
