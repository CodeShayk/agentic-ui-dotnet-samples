using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Ch22.Beacon.AgentFramework;

/// <summary>
/// An Agent Framework session store in plain files: one JSON file per session, holding what the agent itself
/// serializes (<see cref="AIAgent.SerializeSessionAsync"/>). The file name is a hash of the whole key, the
/// session ID (the AG-UI thread ID, under <c>MapAGUIServer</c>) and every partition, because an isolation
/// provider adds partitions and two users must never share a file (Chapter 23).
/// </summary>
public sealed class FileAgentSessionStore(string directory) : AgentSessionStore
{
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        // A new session object on every lookup: callers may change it, and may run branches side by side.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await agent.DeserializeSessionAsync(document.RootElement.Clone(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        var state = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(directory);
        var path = PathFor(key);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var writer = new Utf8JsonWriter(stream);
            state.WriteTo(writer);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(AgentSessionStoreKey key)
    {
        var identity = new StringBuilder(key.SessionId);
        foreach (var (name, value) in (key.Partitions ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            identity.Append('\n').Append(name).Append('=').Append(value);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..32];
        return Path.Combine(directory, $"{hash}.json");
    }
}
