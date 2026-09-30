using Xunit;

// Serial, because a host built through AddObservability subscribes a process-wide listener
// that starts a server Activity for another test class's request; this suite needs no container.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
