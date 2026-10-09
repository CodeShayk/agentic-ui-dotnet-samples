using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.Domain;
using Ch15.Forms;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent.Input;

/// <summary>
/// One thing Beacon can ask a person for. The model sees it as a tool; when the model calls it and the
/// answer isn't already on record, <see cref="InputRequestChatClient"/> turns the call into a request
/// for input instead of running it. The form writes the question (prompt, schema and metadata) from the
/// server's records, so the model chooses <em>what</em> to ask about but never <em>how</em> the form looks.
/// </summary>
public abstract class InputForm
{
    /// <summary>The form's ID in the interrupt's metadata and in the server's records.</summary>
    public abstract string Id { get; }

    /// <summary>The name the model calls.</summary>
    public abstract string ToolName { get; }

    /// <summary>
    /// The model-facing tool. It runs only when no question is needed (the value is on record, or the
    /// arguments don't name anything Beacon would ask about), and then it answers or explains.
    /// </summary>
    public abstract AIFunction CreateTool();

    /// <summary>The question to put to the person, or <see langword="null"/> if there is nothing to ask.</summary>
    public abstract InputQuestion? Ask(IDictionary<string, object?>? arguments);

    /// <summary>Applies a validated answer and returns what the model reads as the call's result.</summary>
    public abstract JsonElement Apply(PendingInput input, JsonElement answer);

    /// <summary>A string argument, whether the model sent it as a string or as JSON.</summary>
    protected static string Argument(IDictionary<string, object?>? arguments, string name) =>
        arguments?.TryGetValue(name, out var value) is true
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
                _ => string.Empty,
            }
            : string.Empty;

    /// <summary>
    /// Builds a question and checks its schema against the subset Beacon's clients render. A server that
    /// asks for something its clients can't show gets an answer nobody chose, so this throws instead.
    /// </summary>
    protected InputQuestion Question(string equipmentId, string message, JsonObject schema, JsonObject metadata)
    {
        var schemaElement = Element(schema);
        if (!FormSchema.TryParse(schemaElement, out var form, out var problems))
        {
            throw new InvalidOperationException($"The {Id} form's schema is outside the supported subset: {string.Join(" ", problems)}");
        }

        return new InputQuestion(Id, equipmentId, message, schemaElement, form, Element(new JsonObject { ["beacon"] = metadata }));
    }

    /// <summary>A result for the model, serialized with Beacon's compact options.</summary>
    protected static JsonElement Result<T>(T value) =>
        JsonSerializer.SerializeToElement(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Ch15Json.Options.GetTypeInfo(typeof(T)));

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}

/// <summary>The forms Beacon can put to a person, by tool name and by ID.</summary>
public sealed class InputForms(IFieldServiceStore store)
{
    /// <summary>Every form, in the order the model is offered them.</summary>
    public IReadOnlyList<InputForm> All { get; } = [new SerialNumberForm(store), new FanMotorReadingForm(store)];

    /// <summary>The model-facing tools.</summary>
    public IList<AITool> CreateTools() => [.. All.Select(form => (AITool)form.CreateTool())];

    /// <summary>The form a call asks through, if it is one of them.</summary>
    public InputForm? ForTool(string toolName) => All.FirstOrDefault(f => f.ToolName == toolName);

    /// <summary>The form with this ID.</summary>
    public InputForm ById(string id) => All.First(f => f.Id == id);

    /// <summary>The question a call asks, or <see langword="null"/> if it is an ordinary call or nothing needs asking.</summary>
    public InputQuestion? Ask(FunctionCallContent call) => ForTool(call.Name)?.Ask(call.Arguments);
}
