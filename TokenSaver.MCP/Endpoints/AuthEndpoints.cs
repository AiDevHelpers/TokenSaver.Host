using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

using AiDevTools.TokenSaver.MCP.Security;

namespace AiDevTools.TokenSaver.MCP.Endpoints;

/// <summary>
///     Manage who may read the endpoint token: the members of the local TokenSaver group. Used by the user
///     picker in the VS Code and Visual Studio settings. These routes sit under /api, so they need the token.
/// </summary>
internal static partial class AuthEndpoints
{
    // DOMAIN\name or name: letters, digits, space, dot, dash, underscore, $ — no path or shell characters.
    [GeneratedRegex(@"^([\w .\-$]{1,64}\\)?[\w .\-$]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountPattern();

    internal static bool IsValidAccount(string? account)
    {
        // No "." / ".." segments: the pattern allows dots inside names but a name made only of dots is not an account.
        return !string.IsNullOrWhiteSpace(account) &&
               AccountPattern().IsMatch(account) &&
               account.Split('\\').All(segment => segment.Trim('.', ' ').Length > 0);
    }

    internal static void MapAuthEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth");

        group.MapGet("/users", (ILocalUserGroup users, AuthTokenStore tokens) => Results.Ok(new
        {
            group = users.Name,
            tokenPath = tokens.Path,
            members = users.Members(),
            candidates = users.CandidateUsers()
        }));

        group.MapPost("/users", ([FromBody] AccountRequest request, ILocalUserGroup users) =>
            Change(request.Account, users, users.Add));

        // DELETE carries the account in the query string: minimal APIs do not bind a body for DELETE.
        group.MapDelete("/users", ([FromQuery] string? account, ILocalUserGroup users) =>
            Change(account, users, users.Remove));
    }

    private static IResult Change(string? account, ILocalUserGroup users, Func<string, string?> apply)
    {
        if (!IsValidAccount(account))
        {
            return Results.BadRequest(new { error = "Account must be a plain user name or DOMAIN\\name." });
        }

        string? error = apply(account!);
        return error is null
            ? Results.Ok(new { group = users.Name, members = users.Members() })
            : Results.BadRequest(new { error });
    }

    internal sealed record AccountRequest(string? Account);
}
