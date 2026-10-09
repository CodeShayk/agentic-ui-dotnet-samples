// Several tests assert on timing (heartbeats every 200 ms, RUN_STARTED before a two-second model, a
// 1.5-second proxy idle timeout). Running them one at a time, with margins of two to six times, keeps
// those results honest on a busy build machine.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
