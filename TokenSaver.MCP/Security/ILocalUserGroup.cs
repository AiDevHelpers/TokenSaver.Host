namespace AiDevTools.TokenSaver.MCP.Security;

/// <summary>
///     The local Windows group whose members may read the endpoint token. The settings pickers in the VS Code
///     and Visual Studio extensions list, add and remove members through the authenticated /api/auth/users API.
/// </summary>
internal interface ILocalUserGroup
{
    /// <summary>Group name, e.g. "TokenSaver Users".</summary>
    string Name { get; }

    /// <summary>Creates the group when it does not exist. Returns false when it cannot be created.</summary>
    bool EnsureExists();

    /// <summary>Members as DOMAIN\name (or MACHINE\name).</summary>
    IReadOnlyList<string> Members();

    /// <summary>Adds an account. Returns an error message, or null on success (also when already a member).</summary>
    string? Add(string account);

    /// <summary>Removes an account. Returns an error message, or null on success (also when not a member).</summary>
    string? Remove(string account);

    /// <summary>Local user accounts that could be added.</summary>
    IReadOnlyList<string> CandidateUsers();
}
