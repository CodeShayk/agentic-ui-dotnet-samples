using System.Text;

namespace Ch20.DeclarativeUi;

/// <summary>How the validator checks a component property, and how a renderer reads it.</summary>
public enum PropKind
{
    /// <summary>A2UI <c>DynamicString</c>: a literal string or a data binding (<c>{"path": ...}</c>).</summary>
    DynamicString,

    /// <summary>A2UI <c>DynamicBoolean</c>: a literal Boolean or a data binding.</summary>
    DynamicBoolean,

    /// <summary>A2UI <c>DynamicNumber</c>: a literal number or a data binding.</summary>
    DynamicNumber,

    /// <summary>A2UI <c>DynamicStringList</c>: a literal array of strings or a data binding.</summary>
    DynamicStringList,

    /// <summary>A literal number, such as a slider's bounds.</summary>
    Number,

    /// <summary>One of a fixed set of strings, such as a text variant.</summary>
    Choice,

    /// <summary>The ID of another component in the surface (A2UI <c>ComponentId</c>).</summary>
    ComponentRef,

    /// <summary>A static array of child IDs, or a template repeated over a data array (A2UI <c>ChildList</c>).</summary>
    ChildList,

    /// <summary>A choice picker's options: <c>[{ "label": ..., "value": ... }]</c>.</summary>
    Options,

    /// <summary>Client-side checks: <c>[{ "condition": ..., "message": ... }]</c>.</summary>
    Checks,

    /// <summary>An action that sends a named event, with context, to the agent.</summary>
    Action,

    /// <summary>An absolute https URL from an allowed origin. Literal only: a binding could change it later.</summary>
    Url,
}

/// <summary>One property a catalog component accepts.</summary>
public sealed record PropSpec(PropKind Kind, bool Required = false, IReadOnlyList<string>? Choices = null);

/// <summary>One component a catalog offers: its name, what it is for and the properties it accepts.</summary>
public sealed record ComponentSpec(string Name, string Description, IReadOnlyDictionary<string, PropSpec> Props)
{
    public IEnumerable<string> RequiredProps => Props.Where(p => p.Value.Required).Select(p => p.Key);

    /// <summary>Properties that name other components: the edges of the surface's tree.</summary>
    public IEnumerable<string> ChildProps => Props.Where(p => p.Value.Kind is PropKind.ComponentRef or PropKind.ChildList).Select(p => p.Key);
}

/// <summary>
/// A component catalog: the only components and functions a surface may use. Producer and consumer agree on
/// it by ID; A2UI's <c>catalogId</c> is an identifier, not a URL anyone has to fetch.
/// </summary>
public sealed class ComponentCatalog(string id, IEnumerable<ComponentSpec> components, IEnumerable<string> functions)
{
    public string Id { get; } = id;

    public IReadOnlyDictionary<string, ComponentSpec> Components { get; } =
        components.ToDictionary(c => c.Name, StringComparer.Ordinal);

    /// <summary>The functions a check may call. Each is pure and implemented by every renderer.</summary>
    public IReadOnlySet<string> Functions { get; } = new HashSet<string>(functions, StringComparer.Ordinal);

    public bool TryGet(string name, out ComponentSpec spec) => Components.TryGetValue(name, out spec!);

    /// <summary>
    /// The catalog as a model reads it: one line per component and the rules that go with them. A2UI v0.9 is
    /// "prompt-first": the catalog travels in the prompt rather than in a structured-output schema.
    /// </summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"Catalog {Id}. Components (* = required property):");
        foreach (var component in Components.Values)
        {
            var props = component.Props.Select(p => p.Value.Choices is { } choices
                ? $"{p.Key}{(p.Value.Required ? "*" : "")}=" + string.Join("|", choices)
                : p.Key + (p.Value.Required ? "*" : ""));
            text.AppendLine($"- {component.Name}({string.Join(", ", props)}): {component.Description}");
        }

        text.AppendLine($"Check functions: {string.Join(", ", Functions)}. No other functions, no functionCall actions.");
        return text.ToString();
    }
}

/// <summary>Beacon's catalog for field forms: a profile of the A2UI v0.9 basic catalog.</summary>
/// <remarks>
/// Every name and property here means what it means in the basic catalog, so a model that knows A2UI
/// writes Beacon's forms without special training. What's left out is deliberate: no <c>Video</c>,
/// <c>AudioPlayer</c>, <c>Modal</c> or <c>Tabs</c> (a form doesn't need them), no <c>validationRegexp</c> (a
/// regular expression from a model would run on the client), and of the basic functions only the pure
/// validation ones: no <c>openUrl</c>, no <c>formatString</c> expression language, no <c>regex</c>.
/// </remarks>
public static class BeaconCatalog
{
    /// <summary>The catalog's ID. The <c>.example</c> domain is reserved, so it can't collide with a real one.</summary>
    public const string Id = "https://beacon.contoso-climate.example/a2ui/catalogs/field-forms/v1";

    private static readonly string[] Justify = ["start", "center", "end", "spaceBetween", "spaceAround", "spaceEvenly", "stretch"];
    private static readonly string[] Align = ["start", "center", "end", "stretch"];

    public static ComponentCatalog FieldForms { get; } = new(Id,
    [
        new("Column", "Stacks its children vertically.", Props(
            ("children", new(PropKind.ChildList, Required: true)),
            ("justify", new(PropKind.Choice, Choices: Justify)),
            ("align", new(PropKind.Choice, Choices: Align)))),
        new("Row", "Places its children side by side.", Props(
            ("children", new(PropKind.ChildList, Required: true)),
            ("justify", new(PropKind.Choice, Choices: Justify)),
            ("align", new(PropKind.Choice, Choices: Align)))),
        new("Card", "A bordered container for one child; wrap several in a Column.", Props(
            ("child", new(PropKind.ComponentRef, Required: true)))),
        new("Text", "Plain text. Variants h1-h5 are headings; caption is small print.", Props(
            ("text", new(PropKind.DynamicString, Required: true)),
            ("variant", new(PropKind.Choice, Choices: ["h1", "h2", "h3", "h4", "h5", "caption", "body"])))),
        new("Divider", "A dividing line.", Props(
            ("axis", new(PropKind.Choice, Choices: ["horizontal", "vertical"])))),
        new("Image", "An image from an allowed https origin.", Props(
            ("url", new(PropKind.Url, Required: true)),
            ("description", new(PropKind.DynamicString)),
            ("fit", new(PropKind.Choice, Choices: ["contain", "cover", "fill", "none", "scaleDown"])),
            ("variant", new(PropKind.Choice, Choices: ["icon", "avatar", "smallFeature", "mediumFeature", "largeFeature", "header"])))),
        new("CheckBox", "A labeled checkbox bound to a Boolean.", Props(
            ("label", new(PropKind.DynamicString, Required: true)),
            ("value", new(PropKind.DynamicBoolean, Required: true)),
            ("checks", new(PropKind.Checks)))),
        new("TextField", "A labeled text input bound to a string. Use variant number for readings.", Props(
            ("label", new(PropKind.DynamicString, Required: true)),
            ("value", new(PropKind.DynamicString)),
            ("variant", new(PropKind.Choice, Choices: ["shortText", "longText", "number"])),
            ("checks", new(PropKind.Checks)))),
        new("ChoicePicker", "Options to pick from, bound to a list of the chosen values.", Props(
            ("label", new(PropKind.DynamicString)),
            ("variant", new(PropKind.Choice, Choices: ["mutuallyExclusive", "multipleSelection"])),
            ("options", new(PropKind.Options, Required: true)),
            ("value", new(PropKind.DynamicStringList, Required: true)),
            ("displayStyle", new(PropKind.Choice, Choices: ["checkbox", "chips"])),
            ("checks", new(PropKind.Checks)))),
        new("Slider", "A numeric value between min and max.", Props(
            ("label", new(PropKind.DynamicString)),
            ("min", new(PropKind.Number)),
            ("max", new(PropKind.Number, Required: true)),
            ("value", new(PropKind.DynamicNumber, Required: true)),
            ("checks", new(PropKind.Checks)))),
        new("Button", "A button whose child is its label (a Text). Its action sends an event to Beacon.", Props(
            ("child", new(PropKind.ComponentRef, Required: true)),
            ("action", new(PropKind.Action, Required: true)),
            ("variant", new(PropKind.Choice, Choices: ["default", "primary", "borderless"])),
            ("checks", new(PropKind.Checks)))),
    ],
    ["required", "length", "numeric", "and", "or", "not"]);

    private static Dictionary<string, PropSpec> Props(params (string Name, PropSpec Spec)[] props) =>
        props.ToDictionary(p => p.Name, p => p.Spec, StringComparer.Ordinal);
}
