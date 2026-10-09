using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// The run an input request belongs to. The handler puts it on the run's <see cref="ChatOptions"/>, so the
/// layer that raises requests inside the chat pipeline knows which thread and run it is in.
/// </summary>
/// <param name="ThreadId">The AG-UI thread.</param>
/// <param name="RunId">The AG-UI run.</param>
public sealed record InputRun(string ThreadId, string RunId)
{
    /// <summary>The key on <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    public const string Key = "beacon.input_run";

    /// <summary>The run these options belong to, if the handler set one.</summary>
    public static InputRun? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(Key, out InputRun? run) is true ? run : null;
}

/// <summary>How long a request for input stays answerable.</summary>
public sealed class InputOptions
{
    public const string SectionName = "Beacon:Inputs";

    /// <summary>
    /// How long the technician has to answer. After that the request can only be abandoned. Fifteen
    /// minutes is long enough to walk out to the unit and back, short enough that a stale answer
    /// doesn't land on a different day's job.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(15);
}
