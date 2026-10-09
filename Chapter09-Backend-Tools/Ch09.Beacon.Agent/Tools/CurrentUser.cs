namespace Ch09.Beacon.Agent.Tools;

/// <summary>The two kinds of people Beacon acts for.</summary>
public enum UserRole
{
    Dispatcher,
    Technician,
}

/// <summary>
/// The person Beacon is acting for. Tools get it from the server, never from their arguments, so
/// nothing the model writes can change who is asking. Beacon Desk has no sign-in until Chapter 23,
/// so in this chapter it comes from configuration (<c>Beacon:User</c>); Chapter 23 builds it from the
/// authenticated user instead, and the tools don't change.
/// </summary>
public sealed record CurrentUser(string Name, UserRole Role, string? TechnicianId = null)
{
    public bool IsDispatcher => Role == UserRole.Dispatcher;

    public static CurrentUser FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Beacon:User");
        var role = Enum.TryParse<UserRole>(section["Role"], ignoreCase: true, out var parsed) ? parsed : UserRole.Dispatcher;
        return new CurrentUser(section["Name"] ?? "Dana Kim", role, section["TechnicianId"]);
    }
}
