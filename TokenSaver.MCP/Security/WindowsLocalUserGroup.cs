using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using static AiDevTools.TokenSaver.MCP.Security.NativeMethods;

namespace AiDevTools.TokenSaver.MCP.Security;

/// <summary>Local group management through netapi32 (no output parsing, no shell).</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsLocalUserGroup(string name, ILogger<WindowsLocalUserGroup> logger) : ILocalUserGroup
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int NerrGroupExists = 2223;
    private const int ErrorAliasExists = 1379;
    private const int ErrorMemberInAlias = 1378;
    private const int ErrorMemberNotInAlias = 1377;
    private const int ErrorNoSuchMember = 1387;
    private const int ErrorNoneMapped = 1332;
    private const int FilterNormalAccount = 0x0002;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local group '{Group}' operation {Operation} failed with error {Code}")]
    private static partial void LogFailed(ILogger logger, string group, string operation, int code);

    public string Name { get; } = name;

    public bool EnsureExists()
    {
        LocalGroupInfo1 info = new() { Name = Name, Comment = "May read the TokenSaver endpoint token and use TokenSaver MCP." };
        int rc = NetLocalGroupAdd(null, 1, ref info, out _);
        if (rc is ErrorSuccess or NerrGroupExists or ErrorAliasExists)
        {
            return true;
        }

        LogFailed(logger, Name, "add-group", rc);
        return false;
    }

    public IReadOnlyList<string> Members()
    {
        List<string> members = [];
        IntPtr resume = IntPtr.Zero;
        int rc;
        do
        {
            rc = NetLocalGroupGetMembers(null, Name, 3, out IntPtr buffer, -1, out int read, out _, ref resume);
            if (rc is not (ErrorSuccess or ErrorMoreData))
            {
                LogFailed(logger, Name, "list-members", rc);
                break;
            }

            try
            {
                int size = Marshal.SizeOf<LocalGroupMembersInfo3>();
                for (int i = 0; i < read; i++)
                {
                    LocalGroupMembersInfo3 m = Marshal.PtrToStructure<LocalGroupMembersInfo3>(buffer + (i * size));
                    if (!string.IsNullOrEmpty(m.DomainAndName))
                    {
                        members.Add(m.DomainAndName);
                    }
                }
            }
            finally
            {
                _ = NetApiBufferFree(buffer);
            }
        }
        while (rc == ErrorMoreData);

        return members;
    }

    public string? Add(string account)
    {
        LocalGroupMembersInfo3 member = new() { DomainAndName = account };
        int rc = NetLocalGroupAddMembers(null, Name, 3, ref member, 1);
        return rc is ErrorSuccess or ErrorMemberInAlias ? null : Describe(rc, account);
    }

    public string? Remove(string account)
    {
        LocalGroupMembersInfo3 member = new() { DomainAndName = account };
        int rc = NetLocalGroupDelMembers(null, Name, 3, ref member, 1);
        return rc is ErrorSuccess or ErrorMemberNotInAlias ? null : Describe(rc, account);
    }

    public IReadOnlyList<string> CandidateUsers()
    {
        List<string> users = [];
        IntPtr resume = IntPtr.Zero;
        int rc;
        do
        {
            rc = NetUserEnum(null, 0, FilterNormalAccount, out IntPtr buffer, -1, out int read, out _, ref resume);
            if (rc is not (ErrorSuccess or ErrorMoreData))
            {
                break;
            }

            try
            {
                int size = Marshal.SizeOf<UserInfo0>();
                for (int i = 0; i < read; i++)
                {
                    UserInfo0 u = Marshal.PtrToStructure<UserInfo0>(buffer + (i * size));
                    if (!string.IsNullOrEmpty(u.Name))
                    {
                        users.Add($"{Environment.MachineName}\\{u.Name}");
                    }
                }
            }
            finally
            {
                _ = NetApiBufferFree(buffer);
            }
        }
        while (rc == ErrorMoreData);

        return users;
    }

    private string Describe(int code, string account)
    {
        LogFailed(logger, Name, "member-change", code);
        return code is ErrorNoSuchMember or ErrorNoneMapped
            ? $"No such account: '{account}'."
            : $"Could not change group membership for '{account}' (error {code}).";
    }
}
