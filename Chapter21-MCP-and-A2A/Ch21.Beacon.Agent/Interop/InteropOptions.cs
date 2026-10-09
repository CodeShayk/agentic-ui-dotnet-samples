namespace Ch21.Beacon.Agent.Interop;

/// <summary>Where the manuals MCP server lives and how much Beacon trusts it to take (the <c>Manuals</c> section).</summary>
public sealed class ManualsOptions
{
    public const string SectionName = "Manuals";

    /// <summary>The server's Streamable HTTP endpoint.</summary>
    public Uri Endpoint { get; set; } = new("http://localhost:5212/mcp");

    /// <summary>How long connecting, listing tools or one tool call may take.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>After a failed connection, how long runs go without manual tools before Beacon tries again.</summary>
    public int RetryAfterSeconds { get; set; } = 30;

    /// <summary>The most text one tool result may hand the model.</summary>
    public int MaxResultCharacters { get; set; } = 4000;
}

/// <summary>Where Northwind Supply's A2A agent lives (the <c>Supplier</c> section).</summary>
public sealed class SupplierOptions
{
    public const string SectionName = "Supplier";

    /// <summary>
    /// The agent's JSON-RPC endpoint, pinned in configuration. Beacon doesn't follow the URL an agent
    /// card advertises: the card is the remote party describing itself.
    /// </summary>
    public Uri Endpoint { get; set; } = new("http://localhost:5213/a2a");

    /// <summary>How long one request to the supplier's agent may take.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Names of the <see cref="IHttpClientFactory"/> clients Beacon uses to reach other systems.</summary>
public static class InteropHttpClients
{
    public const string Manuals = "manuals-mcp";
    public const string Supplier = "supplier-a2a";
}
