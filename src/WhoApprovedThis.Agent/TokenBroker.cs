using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;

namespace WhoApprovedThis.Agent;

// AgentCore Identity acts as the token broker. Given the workload access
// token the runtime derived from the caller's JWT, it returns a user-scoped
// access token for the expense MCP server from its token vault. On first use
// there is nothing in the vault yet, so it returns an authorization URL where
// the user grants consent; after that, tokens are cached per user.
public class TokenBroker
{
    static readonly string Provider =
        Environment.GetEnvironmentVariable("CREDENTIAL_PROVIDER") ?? "cognito-expenses";
    // openid is required for Cognito to include identity claims, such as
    // cognito:groups, in the access token
    static readonly List<string> Scopes =
        ["openid", "expenses/read", "expenses/write", "expenses/approve"];
    // Where the user's browser lands after granting consent (session binding)
    static readonly string ReturnUrl =
        Environment.GetEnvironmentVariable("APP_URL") ?? "http://localhost:4000/";

    readonly AmazonBedrockAgentCoreClient _client = new();

    public async Task<(string? UserToken, string? AuthorizationUrl)> GetUserToken(
        string workloadToken)
    {
        var response = await Request(workloadToken, force: false);

        // The vault returns the stored grant even if the requested scopes
        // have widened since the user consented; a fresh consent is needed
        if (response.AccessToken is { } token && !CoversScopes(token))
            response = await Request(workloadToken, force: true);

        return (response.AccessToken, response.AuthorizationUrl);
    }

    Task<GetResourceOauth2TokenResponse> Request(string workloadToken, bool force) =>
        _client.GetResourceOauth2TokenAsync(new GetResourceOauth2TokenRequest
        {
            WorkloadIdentityToken = workloadToken,
            ResourceCredentialProviderName = Provider,
            Scopes = Scopes,
            Oauth2Flow = Oauth2FlowType.USER_FEDERATION,
            ResourceOauth2ReturnUrl = ReturnUrl,
            ForceAuthentication = force,
        });

    static bool CoversScopes(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var claims = JsonDocument.Parse(
            Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
        var granted = claims.RootElement.TryGetProperty("scope", out var scope)
            ? scope.GetString()!.Split(' ')
            : [];
        return Scopes.Where(s => s != "openid").All(granted.Contains);
    }
}
