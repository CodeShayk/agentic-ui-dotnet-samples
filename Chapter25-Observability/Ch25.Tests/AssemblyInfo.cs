// Activity sources and the System.Net.Http source are process-wide, so every in-memory host's
// tracer provider would also see the spans of hosts running in parallel. One test at a time keeps
// each host's exporter to its own spans.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
