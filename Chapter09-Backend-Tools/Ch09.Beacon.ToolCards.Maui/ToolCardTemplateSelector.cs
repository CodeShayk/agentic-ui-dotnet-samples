namespace Ch09.Beacon.ToolCards.Maui;

/// <summary>
/// Beacon Field's tool cards: a native template per kind of result, chosen per item. It plays the part
/// the <c>switch</c> in the Desk's <c>ToolCallList</c> plays, from the same <see cref="ToolCard"/> models,
/// and falls back the same way: a call that is still running, failed or stopped, or that returned a
/// result this client has no card for, gets the status chip alone.
/// </summary>
/// <remarks>
/// A Field page declares the templates in XAML, binds each one's text to the card (every string in a
/// card came from the model or a tool, so it is only ever text) and sets
/// <c>SemanticProperties.Description</c> to <see cref="ToolCard.Headline"/>, so a screen reader hears the
/// status in words. The page rebuilds its list from <c>ToolCards.From</c> when the conversation changes,
/// on the main thread, as Chapter 8's Field does for its transcript.
/// </remarks>
public sealed class ToolCardTemplateSelector : DataTemplateSelector
{
    /// <summary>The status chip on its own: the headline, the state in words and, for a failure, the error.</summary>
    public DataTemplate Chip { get; set; } = default!;

    public DataTemplate WorkOrder { get; set; } = default!;

    public DataTemplate Parts { get; set; } = default!;

    public DataTemplate Warranty { get; set; } = default!;

    public DataTemplate Checklist { get; set; } = default!;

    /// <summary>A result that isn't JSON, shown as text under the chip.</summary>
    public DataTemplate Text { get; set; } = default!;

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        ToolCard { Body: WorkOrderBody } => WorkOrder,
        ToolCard { Body: PartsBody } => Parts,
        ToolCard { Body: WarrantyBody } => Warranty,
        ToolCard { Body: ChecklistBody } => Checklist,
        ToolCard { Body: TextBody } => Text,
        _ => Chip,
    };
}
