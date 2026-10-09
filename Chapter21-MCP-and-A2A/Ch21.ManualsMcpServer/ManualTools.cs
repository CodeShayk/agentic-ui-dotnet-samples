using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Ch21.ManualsMcpServer;

/// <summary>
/// The manual library's MCP tools. Two read-only tools for looking things up, and one write tool
/// (corrections) that exists because real servers expose more than any one client should use.
/// </summary>
[McpServerToolType]
public sealed class ManualTools
{
    public const string SearchManualName = "search_manual";
    public const string GetManualSectionName = "get_manual_section";
    public const string SubmitCorrectionName = "submit_manual_correction";

    [McpServerTool(Name = SearchManualName, Title = "Search a service manual", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the service manual for one equipment model and returns matching section IDs and titles.")]
    public static string SearchManual(
        [Description("The equipment model, for example CA-36X, HP-24S, RT-120 or FX-90.")] string model,
        [Description("What to look for, for example 'locked rotor' or 'capacitor'.")] string query)
    {
        var manual = ManualLibrary.Find(model)
            ?? throw new McpException($"There is no manual for model '{model}'. Known models: {string.Join(", ", ManualLibrary.Models)}.");

        var hits = ManualLibrary.Search(manual, query);
        if (hits.Count == 0)
        {
            return $"No sections of the {manual.Title} match \"{query}\".";
        }

        var text = new StringBuilder($"{hits.Count} section(s) of the {manual.Title} match \"{query}\":\n");
        foreach (var section in hits)
        {
            text.Append($"\n- {section.Id} {section.Title}");
        }

        return text.ToString();
    }

    [McpServerTool(Name = GetManualSectionName, Title = "Read a manual section", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gets the full text of one manual section by its ID, for example RT-120/7.3.")]
    public static string GetManualSection(
        [Description("The section ID returned by search_manual, for example RT-120/7.3.")] string sectionId)
    {
        var (manual, section) = ManualLibrary.Section(sectionId)
            ?? throw new McpException($"There is no manual section '{sectionId}'.");

        return $"# {manual.Title}\n## {section.Id} {section.Title}\n\n{section.Text}";
    }

    [McpServerTool(Name = SubmitCorrectionName, Title = "Submit a manual correction", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Submits a correction to a manual section for the documentation team to review.")]
    public static string SubmitManualCorrection(
        [Description("The section ID the correction applies to.")] string sectionId,
        [Description("The proposed correction.")] string correction)
    {
        _ = ManualLibrary.Section(sectionId) ?? throw new McpException($"There is no manual section '{sectionId}'.");
        return $"Correction to {sectionId} queued for review ({correction.Length} characters).";
    }
}
