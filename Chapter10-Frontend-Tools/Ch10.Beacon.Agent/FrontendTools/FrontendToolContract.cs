using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ch10.Beacon.Agent.FrontendTools;

/// <summary>
/// What Beacon accepts back from one frontend tool: a size limit, a typed shape and the rules its
/// values must follow. The client runs the tool, so its result is client input, whatever the tool's
/// name promises.
/// </summary>
public sealed class FrontendToolContract
{
    /// <summary>The longest result text Beacon reads, in characters, unless a contract says otherwise.</summary>
    public const int DefaultMaxResultLength = 1_024;

    private readonly Func<string, FrontendResultCheck> _check;

    private FrontendToolContract(string name, int maxResultLength, Func<string, FrontendResultCheck> check) =>
        (Name, MaxResultLength, _check) = (name, maxResultLength, check);

    /// <summary>The tool's name, as the client declares it in <c>RunAgentInput.tools</c>.</summary>
    public string Name { get; }

    /// <summary>The longest result text this contract reads before rejecting it unparsed.</summary>
    public int MaxResultLength { get; }

    /// <summary>Creates a contract whose result must deserialize as <typeparamref name="T"/> and pass <paramref name="rules"/>.</summary>
    /// <param name="name">The tool's name.</param>
    /// <param name="shape">Source-generated metadata for the result type: the only fields the model will ever see.</param>
    /// <param name="rules">Returns why a parsed result is unacceptable, or <see langword="null"/> when it is fine. Never echo client text in the reason.</param>
    /// <param name="maxResultLength">The size limit, in characters.</param>
    public static FrontendToolContract For<T>(string name, JsonTypeInfo<T> shape, Func<T, string?> rules, int maxResultLength = DefaultMaxResultLength)
        where T : class =>
        new(name, maxResultLength, text =>
        {
            if (text.Length > maxResultLength)
            {
                return FrontendResultCheck.Reject($"the result is {text.Length} characters long; the limit is {maxResultLength}");
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize(text, shape);
            }
            catch (JsonException)
            {
                return FrontendResultCheck.Reject("the result isn't the JSON object Beacon expects from this tool");
            }

            if (value is null)
            {
                return FrontendResultCheck.Reject("the result is empty");
            }

            if (rules(value) is { } problem)
            {
                return FrontendResultCheck.Reject(problem);
            }

            // Parse, then re-serialize: the model sees the fields Beacon declared and nothing else the
            // client added, such as a "note" written to steer it.
            return FrontendResultCheck.Accept(JsonSerializer.SerializeToElement(value, shape));
        });

    /// <summary>Checks one result text.</summary>
    public FrontendResultCheck Check(string resultText)
    {
        ArgumentNullException.ThrowIfNull(resultText);
        return _check(resultText);
    }
}

/// <summary>The outcome of checking a frontend tool result.</summary>
/// <param name="Accepted">The normalized result the model may see, when it passed.</param>
/// <param name="Problem">Why it was rejected, when it didn't. Written by the server, never copied from the result.</param>
public readonly record struct FrontendResultCheck(JsonElement? Accepted, string? Problem)
{
    public bool IsAccepted => Accepted is not null;

    public static FrontendResultCheck Accept(JsonElement normalized) => new(normalized, null);

    public static FrontendResultCheck Reject(string problem) => new(null, problem);
}
