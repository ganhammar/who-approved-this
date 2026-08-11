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
    static readonly List<string> Scopes =
        ["expenses/read", "expenses/write", "expenses/approve"];

    readonly AmazonBedrockAgentCoreClient _client = new();

    public async Task<(string? UserToken, string? AuthorizationUrl)> GetUserToken(
        string workloadToken)
    {
        var response = await _client.GetResourceOauth2TokenAsync(
            new GetResourceOauth2TokenRequest
            {
                WorkloadIdentityToken = workloadToken,
                ResourceCredentialProviderName = Provider,
                Scopes = Scopes,
                Oauth2Flow = Oauth2FlowType.USER_FEDERATION,
            });

        return (response.AccessToken, response.AuthorizationUrl);
    }
}
