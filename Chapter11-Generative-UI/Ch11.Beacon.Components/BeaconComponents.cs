namespace Ch11.Beacon.Components;

/// <summary>
/// Beacon's component catalog: the only components the agent may show. The server offers exactly these
/// tools to the model, and a client renders a component only if it knows its name.
/// </summary>
/// <remarks>
/// The schemas are written by hand because they are the contract the model works to: flat, with every
/// constraint stated, and without <c>oneOf</c> or <c>$ref</c>, which providers support unevenly.
/// </remarks>
public static class BeaconComponents
{
    public static ComponentContract PartCard { get; } = new(
        ToolName: "show_part_card",
        ComponentName: "PartCard",
        Description:
            "Shows one stocked part as a card the dispatcher can act on. Use it when the answer is a single part. " +
            "Pass only values you got from search_parts, and the work order the part is for, if any.",
        Schema: ComponentContract.ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "sku": { "type": "string", "description": "The part SKU from search_parts, for example CAP-45-5.", "pattern": "^[A-Z]{3}-[A-Z0-9]+(-[A-Z0-9]+)?$" },
                "name": { "type": "string", "description": "The part name.", "minLength": 1, "maxLength": 80 },
                "unitPrice": { "type": "number", "description": "Price per unit in US dollars.", "minimum": 0, "maximum": 100000 },
                "inStock": { "type": "integer", "description": "Units in stock.", "minimum": 0 },
                "warehouse": { "type": "string", "description": "Where the stock is.", "maxLength": 40 },
                "workOrderId": { "type": "string", "description": "The work order the part is for, for example WO-1001.", "pattern": "^WO-[0-9]{4}$" },
                "note": { "type": "string", "description": "One short sentence on why this part.", "maxLength": 160 }
              },
              "required": ["sku", "name", "unitPrice", "inStock"],
              "additionalProperties": false
            }
            """),
        PropsType: ComponentJsonContext.Default.PartCardProps);

    public static ComponentContract ScheduleTimeline { get; } = new(
        ToolName: "show_schedule_timeline",
        ComponentName: "ScheduleTimeline",
        Description:
            "Shows one day's schedule as a timeline with a lane per technician. Use it for questions about who " +
            "is busy or free. Pass only slots you got from get_schedule; leave workOrderId out for free time.",
        Schema: ComponentContract.ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "date": { "type": "string", "description": "The day shown, as YYYY-MM-DD.", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "format": "date" },
                "title": { "type": "string", "maxLength": 80 },
                "lanes": {
                  "type": "array", "minItems": 1, "maxItems": 8,
                  "description": "One lane per technician, in display order.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "technicianId": { "type": "string", "pattern": "^TECH-[0-9]+$" },
                      "technicianName": { "type": "string", "maxLength": 60 },
                      "slots": {
                        "type": "array", "maxItems": 12,
                        "items": {
                          "type": "object",
                          "properties": {
                            "start": { "type": "string", "description": "Start time, HH:mm.", "pattern": "^([01][0-9]|2[0-3]):[0-5][0-9]$" },
                            "end": { "type": "string", "description": "End time, HH:mm.", "pattern": "^([01][0-9]|2[0-3]):[0-5][0-9]$" },
                            "workOrderId": { "type": "string", "pattern": "^WO-[0-9]{4}$" },
                            "label": { "type": "string", "maxLength": 40 }
                          },
                          "required": ["start", "end"],
                          "additionalProperties": false
                        }
                      }
                    },
                    "required": ["technicianId", "slots"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["date", "lanes"],
              "additionalProperties": false
            }
            """),
        PropsType: ComponentJsonContext.Default.ScheduleTimelineProps);

    public static ComponentContract FaultHistoryChart { get; } = new(
        ToolName: "show_fault_history_chart",
        ComponentName: "FaultHistoryChart",
        Description:
            "Charts the recorded faults of one piece of equipment over time. Use it when the user asks how often " +
            "or when a unit has faulted. Pass only faults you got from get_fault_history.",
        Schema: ComponentContract.ParseSchema(
            """
            {
              "type": "object",
              "properties": {
                "equipmentId": { "type": "string", "description": "The equipment ID, for example EQ-501.", "pattern": "^EQ-[0-9]{3}$" },
                "title": { "type": "string", "maxLength": 80 },
                "faults": {
                  "type": "array", "minItems": 1, "maxItems": 50,
                  "items": {
                    "type": "object",
                    "properties": {
                      "date": { "type": "string", "pattern": "^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "format": "date" },
                      "code": { "type": "string", "pattern": "^[A-Z][0-9]{1,3}$" },
                      "description": { "type": "string", "maxLength": 80 }
                    },
                    "required": ["date", "code"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["equipmentId", "faults"],
              "additionalProperties": false
            }
            """),
        PropsType: ComponentJsonContext.Default.FaultHistoryChartProps);

    /// <summary>The whole catalog: the allow-list.</summary>
    public static IReadOnlyList<ComponentContract> All { get; } = [PartCard, ScheduleTimeline, FaultHistoryChart];

    /// <summary>Finds a contract by the tool name the model called.</summary>
    public static ComponentContract? FindByTool(string toolName) =>
        All.FirstOrDefault(c => string.Equals(c.ToolName, toolName, StringComparison.Ordinal));
}
