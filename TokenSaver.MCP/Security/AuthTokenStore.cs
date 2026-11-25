using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using Microsoft.Extensions.Options;

using AiDevTools.TokenSaver.Common.Core.Configuration;

namespace AiDevTools.TokenSaver.MCP.Security;

/// <summary>
///     The per-install bearer token every client sends to /mcp and /api. Generated once, stored in a file
///     only SYSTEM, Administrators and the TokenSaver user group can read, and loaded again on later starts.
/// </summary>
internal sealed partial class AuthTokenStore
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint token created at {Path}")]
    private static partial void LogCreated(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not restrict the access list of {Path}; the token file keeps its inherited permissions")]
    private static partial void LogAclFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Token path {Path} is not writable; using {Fallback}")]
    private static partial void LogFallback(ILogger logger, string path, string fallback);

    private readonly byte[] _tokenBytes;

    public AuthTokenStore(IOptions<TokenSaverConfig> config, ILocalUserGroup group, ILogger<AuthTokenStore> logger)
    {
        (string path, string token) = LoadOrCreate(ResolvePath(config.Value.AuthTokenPath), group, logger);
        Path = path;
        Token = token;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
    }

    /// <summary>Location of the token file clients read.</summary>
    public string Path { get; }

    internal string Token { get; }

    /// <summary>Constant-time comparison of a presented token with the real one.</summary>
    public bool IsValid(string? presented)
    {
        return !string.IsNullOrEmpty(presented) &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _tokenBytes);
    }

    internal static string ResolvePath(string configured)
    {
        return string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TokenSaver", "auth-token")
            : Environment.ExpandEnvironmentVariables(configured);
    }

    private static (string Path, string Token) LoadOrCreate(string path, ILocalUserGroup group, ILogger logger)
    {
        try
        {
            return LoadOrCreateAt(path, group, logger);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A dev run without rights to ProgramData keeps its token under the user's profile instead.
            string fallback = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TokenSaver", "auth-token");
            LogFallback(logger, path, fallback);
            return LoadOrCreateAt(fallback, group, logger);
        }
    }

    private static (string Path, string Token) LoadOrCreateAt(string path, ILocalUserGroup group, ILogger logger)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
            {
                return (path, existing);
            }
        }

        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        File.WriteAllText(path, token);
        Restrict(path, group, logger);
        LogCreated(logger, path);
        return (path, token);
    }

    private static void Restrict(string path, ILocalUserGroup group, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            FileSecurity security = new();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddRule(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
            AddRule(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
            // The account that created the file keeps access: a dev instance run as a normal user must be able
            // to read its own token on the next start.
            if (WindowsIdentity.GetCurrent().User is { } self)
            {
                AddRule(security, self, FileSystemRights.FullControl);
            }

            if (group.EnsureExists())
            {
                AddRule(security, new NTAccount(group.Name), FileSystemRights.Read);
            }

            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or IdentityNotMappedException or PlatformNotSupportedException)
        {
            LogAclFailed(logger, ex, path);
        }
    }

    private static void AddRule(FileSecurity security, IdentityReference who, FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(who, rights, AccessControlType.Allow));
    }
}
