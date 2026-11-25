using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiDevTools.TokenSaver.MCP.Security;

/// <summary>netapi32 entry points for local group management.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LocalGroupInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct LocalGroupMembersInfo3
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DomainAndName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct UserInfo0
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupAdd(string? server, int level, ref LocalGroupInfo1 buffer, out int parmError);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupGetMembers(
        string? server, string group, int level, out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref LocalGroupMembersInfo3 buffer, int totalEntries);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetLocalGroupDelMembers(string? server, string group, int level, ref LocalGroupMembersInfo3 buffer, int totalEntries);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetUserEnum(
        string? server, int level, int filter, out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr buffer);
}
