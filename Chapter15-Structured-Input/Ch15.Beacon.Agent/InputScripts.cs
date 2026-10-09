using System.Globalization;
using System.Text.Json;
using Beacon.AI;
using Beacon.Domain;
using Ch15.Beacon.Agent.Input;
using Microsoft.Extensions.AI;

namespace Ch15.Beacon.Agent;

/// <summary>
/// The scripted model's behaviors for this chapter, tried before <see cref="BeaconScripts.Default"/>. A real
/// model decides for itself when to ask; the scripts make the WO-1003 story repeatable: the warranty check
/// fails for want of a serial number, Beacon asks for it and for a fan motor reading in one run, and once
/// the answers arrive it checks the warranty and recommends a part.
/// </summary>
public static class InputScripts
{
    /// <summary>"What's the serial number of EQ-601?": ask for it (the call passes through when it's on record).</summary>
    public static ScriptRule SerialNumber { get; } = new("serial-number", Reply: ctx =>
        ctx.Mentions("serial") && EquipmentIn(ctx) is { } equipmentId
            ? ScriptReply.Call(SerialNumberForm.Name, Args(equipmentId))
            : null);

    /// <summary>check_warranty says the serial number is missing: ask for it, and for a reading if the fan is the trouble.</summary>
    public static ScriptRule AskForMissingDetails { get; } = new("ask-for-missing-details", Summarize: ctx =>
    {
        var missing = ctx.ToolResults.FirstOrDefault(r => ctx.ToolNameFor(r) == BeaconTools.CheckWarrantyName
            && ScriptedChatClient.ResultJson(r).Contains("no serial number", StringComparison.OrdinalIgnoreCase));
        if (missing is null || ArgumentOf(ctx, missing.CallId, "equipmentId") is not { } equipmentId)
        {
            return null;
        }

        List<ScriptToolCall> calls = [new(SerialNumberForm.Name, Args(equipmentId))];
        var text = $"{equipmentId} has no serial number on record, and Contoso Air looks warranty up by serial number, so I need it from the nameplate.";
        if (ctx.Mentions("fan", "motor", "bearing", "grinding"))
        {
            calls.Add(new ScriptToolCall(FanMotorReadingForm.Name, Args(equipmentId)));
            text += " I also need a reading from the outdoor fan motor to choose between a new motor and a bearing kit.";
        }

        return new ScriptReply { Text = text, ToolCalls = calls };
    });

    /// <summary>The answers came back: check the warranty if there's a serial number now, otherwise advise from what there is.</summary>
    public static ScriptRule AfterAnswers { get; } = new("after-answers", Summarize: ctx =>
    {
        var serial = LatestResult(ctx, ctx.ToolResults, SerialNumberForm.Name);
        var reading = LatestResult(ctx, ctx.ToolResults, FanMotorReadingForm.Name);
        if (serial is null && reading is null)
        {
            return null;
        }

        List<string> lines = [];
        List<ScriptToolCall> calls = [];
        if (serial is { } recorded && recorded.TryGetProperty("recorded", out _))
        {
            var equipmentId = recorded.GetProperty("equipmentId").GetString()!;
            lines.Add($"Thanks. {equipmentId} now has serial number {recorded.GetProperty("serialNumber").GetString()} on record, so I can check the warranty.");
            calls.Add(new ScriptToolCall(BeaconTools.CheckWarrantyName, Args(equipmentId)));
        }
        else if (serial is { } other)
        {
            lines.Add(IsAbandoned(other)
                ? "Understood. Without the serial number I can't check the warranty with Contoso Air."
                : $"I couldn't record the serial number: {Error(other)}");
        }

        if (calls.Count == 0 && reading is { } measured)
        {
            lines.Add(Recommendation(measured, covered: null));
        }

        return new ScriptReply { Text = string.Join(' ', lines), ToolCalls = calls };
    });

    /// <summary>The warranty is known and this turn has a reading: answer both questions.</summary>
    public static ScriptRule WarrantyAndAdvice { get; } = new("warranty-and-advice", Summarize: ctx =>
    {
        var warranty = ctx.ToolResults.FirstOrDefault(r => ctx.ToolNameFor(r) == BeaconTools.CheckWarrantyName);
        if (warranty is null || Parse(warranty) is not { } status || !status.TryGetProperty("covered", out var covered)
            || LatestResult(ctx, ResultsSinceLastUserMessage(ctx), FanMotorReadingForm.Name) is not { } reading)
        {
            return null; // without a reading, the default summary describes the warranty
        }

        var equipmentId = status.GetProperty("equipmentId").GetString();
        var text = covered.GetBoolean()
            ? $"{equipmentId} is under warranty until {DateOnly.Parse(status.GetProperty("expiresOn").GetString()!, CultureInfo.InvariantCulture):MMMM d, yyyy}: {status.GetProperty("coverage").GetString()}."
            : $"{equipmentId} is not under warranty ({status.GetProperty("coverage").GetString()}).";

        return ScriptReply.Say($"{text} {Recommendation(reading, covered.GetBoolean())}");
    });

    public static IReadOnlyList<ScriptRule> Rules { get; } = [SerialNumber, AskForMissingDetails, AfterAnswers, WarrantyAndAdvice];

    // The motor, the bearings or neither, from the reading: what a technician would conclude.
    private static string Recommendation(JsonElement reading, bool? covered)
    {
        if (IsAbandoned(reading))
        {
            return "Without a reading I can't tell a failing motor from worn bearings; the bearing kit is the cheaper place to start.";
        }

        if (reading.TryGetProperty("error", out _))
        {
            return $"I couldn't use the fan reading: {Error(reading)}";
        }

        var equipmentId = reading.GetProperty("equipmentId").GetString()!;
        var amps = reading.GetProperty("amps").GetDecimal();
        decimal? rated = reading.TryGetProperty("ratedAmps", out var r) ? r.GetDecimal() : null;
        var noise = reading.GetProperty("noise").GetString();
        var play = reading.GetProperty("shaftPlay").GetBoolean();
        var model = SeedData.Equipment.FirstOrDefault(e => e.Id == equipmentId)?.Model ?? string.Empty;
        var ratedText = rated is { } value ? $" against its {Number(value)} A rating" : string.Empty;
        var partsCovered = covered == true ? " Parts are covered by the warranty." : string.Empty;

        if (play || (rated is { } limit && amps > limit * 1.1m))
        {
            var motor = Part(model, "fan motor");
            return $"The motor draws {Number(amps)} A{ratedText}{(play ? " and its shaft has play" : string.Empty)}, so replace the motor" +
                (motor is null ? "." : $": {motor.Sku}, {motor.Name} (${motor.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)}).") + partsCovered;
        }

        if (noise is "grinding" or "squealing")
        {
            var bearings = Part(model, "bearing");
            return $"The motor draws {Number(amps)} A{ratedText}, so the {noise} is the bearings: replace them" +
                (bearings is null ? "" : $" with {bearings.Sku}, {bearings.Name} (${bearings.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)}),") +
                " and keep the motor." + partsCovered;
        }

        return $"The motor draws {Number(amps)} A{ratedText} and sounds {noise}, so it looks healthy; check the fan blade and guard for the noise.";
    }

    private static Part? Part(string model, string kind) =>
        SeedData.Parts.FirstOrDefault(p => p.Name.Contains(kind, StringComparison.OrdinalIgnoreCase)
            && p.CompatibleModels.Contains(model, StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, object?> Args(string equipmentId) => new() { ["equipmentId"] = equipmentId };

    private static string? EquipmentIn(ScriptContext ctx) =>
        ctx.Find(BeaconScripts.EquipmentPattern)
        ?? (ctx.Find(BeaconScripts.WorkOrderPattern) is { } workOrderId
            ? SeedData.WorkOrders.FirstOrDefault(w => w.Id == workOrderId)?.EquipmentId
            : null);

    // A string argument of an earlier call, found by its ID.
    private static string? ArgumentOf(ScriptContext ctx, string callId, string name) =>
        ctx.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().LastOrDefault(c => c.CallId == callId)?.Arguments is { } arguments
        && arguments.TryGetValue(name, out var value)
            ? value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            }
            : null;

    private static JsonElement? LatestResult(ScriptContext ctx, IEnumerable<FunctionResultContent> results, string toolName) =>
        results.LastOrDefault(r => ctx.ToolNameFor(r) == toolName) is { } result ? Parse(result) : null;

    // Results since the person last spoke: a resumed run's answers stay in view after later tool calls.
    private static IEnumerable<FunctionResultContent> ResultsSinceLastUserMessage(ScriptContext ctx)
    {
        var lastUser = -1;
        for (var i = 0; i < ctx.Messages.Count; i++)
        {
            if (ctx.Messages[i].Role == ChatRole.User)
            {
                lastUser = i;
            }
        }

        return ctx.Messages.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>();
    }

    private static JsonElement? Parse(FunctionResultContent result)
    {
        try
        {
            using var document = JsonDocument.Parse(ScriptedChatClient.ResultJson(result));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsAbandoned(JsonElement result) =>
        result.TryGetProperty("status", out var status) && status.GetString() == "abandoned";

    private static string Error(JsonElement result) =>
        result.TryGetProperty("error", out var error) ? error.GetString() ?? "unknown error" : "unknown error";

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
