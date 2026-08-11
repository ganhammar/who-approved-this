using Amazon.CDK;
using Amazon.CDK.AWS.BedrockAgentCore;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Constructs;
using AgentRuntime = Amazon.CDK.AWS.BedrockAgentCore.Runtime;
using Attribute = Amazon.CDK.AWS.DynamoDB.Attribute;
using Function = Amazon.CDK.AWS.Lambda.Function;
using FunctionProps = Amazon.CDK.AWS.Lambda.FunctionProps;
using LambdaRuntime = Amazon.CDK.AWS.Lambda.Runtime;
using PolicyStatement = Amazon.CDK.AWS.IAM.PolicyStatement;
using PolicyStatementProps = Amazon.CDK.AWS.IAM.PolicyStatementProps;

namespace WhoApprovedThis.Infrastructure;

public class WhoApprovedThisStack : Stack
{
    internal WhoApprovedThisStack(Construct scope, string id, IStackProps? props = null)
        : base(scope, id, props)
    {
        // --- Cognito: the authorization server for both ends of the flow ---
        var pool = new UserPool(this, "UserPool", new UserPoolProps
        {
            UserPoolName = "who-approved-this",
            SelfSignUpEnabled = false,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        var domain = pool.AddDomain("Domain", new UserPoolDomainOptions
        {
            CognitoDomain = new CognitoDomainOptions
            {
                DomainPrefix = $"who-approved-this-{Account}",
            },
        });

        ResourceServerScope Scope(string name, string description) =>
            new(new ResourceServerScopeProps { ScopeName = name, ScopeDescription = description });

        var read = Scope("read", "List expenses");
        var write = Scope("write", "Submit expenses");
        var approve = Scope("approve", "Approve expenses");

        var resourceServer = pool.AddResourceServer("ExpensesApi", new UserPoolResourceServerOptions
        {
            Identifier = "expenses",
            Scopes = [read, write, approve],
        });

        var managers = new CfnUserPoolGroup(this, "Managers", new CfnUserPoolGroupProps
        {
            UserPoolId = pool.UserPoolId,
            GroupName = "managers",
        });

        foreach (var name in new[] { "alice", "bob" })
        {
            var user = new CfnUserPoolUser(this, $"User-{name}", new CfnUserPoolUserProps
            {
                UserPoolId = pool.UserPoolId,
                Username = name,
                MessageAction = "SUPPRESS",
            });
            if (name == "bob")
            {
                var membership = new CfnUserPoolUserToGroupAttachment(this, "BobIsManager",
                    new CfnUserPoolUserToGroupAttachmentProps
                    {
                        UserPoolId = pool.UserPoolId,
                        Username = name,
                        GroupName = managers.GroupName!,
                    });
                membership.AddResourceDependency(user);
                membership.AddResourceDependency(managers);
            }
        }

        // --- Frontend hosting (client is created after the distribution,
        // whose domain becomes the OAuth callback URL) ---
        var site = new Bucket(this, "Site", new BucketProps
        {
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
        });

        var distribution = new Distribution(this, "Distribution", new DistributionProps
        {
            DefaultBehavior = new BehaviorOptions
            {
                Origin = S3BucketOrigin.WithOriginAccessControl(site),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
            },
            DefaultRootObject = "index.html",
        });

        // Same-origin proxy for InvokeAgentRuntime, so the browser needs no
        // CORS support from the AgentCore endpoint and no AWS SDK
        distribution.AddBehavior("/runtimes/*",
            new HttpOrigin($"bedrock-agentcore.{Region}.amazonaws.com", new HttpOriginProps
            {
                // Default 30s is shorter than the agent's cold start
                ReadTimeout = Duration.Seconds(60),
            }),
            new AddBehaviorOptions
            {
                AllowedMethods = AllowedMethods.ALLOW_ALL,
                CachePolicy = CachePolicy.CACHING_DISABLED,
                OriginRequestPolicy = OriginRequestPolicy.ALL_VIEWER_EXCEPT_HOST_HEADER,
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
            });

        // The human's client: login only, no expense scopes. The user's own
        // token never carries API permissions - that is the point
        var frontendClient = pool.AddClient("Frontend", new UserPoolClientOptions
        {
            // AdminUserPassword lets test scripts mint tokens without a browser
            AuthFlows = new AuthFlow { UserSrp = true, AdminUserPassword = true },
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows { AuthorizationCodeGrant = true },
                Scopes = [OAuthScope.OPENID, OAuthScope.PROFILE],
                CallbackUrls =
                [
                    $"https://{distribution.DistributionDomainName}/",
                    "http://localhost:4000/",
                ],
            },
        });

        // The agent's client: holds the expense scopes. AgentCore Identity
        // redeems codes against it on the user's behalf. Its real callback
        // URL is only known once the credential provider exists, so the
        // deploy script appends it after creation
        var agentClient = pool.AddClient("Agent", new UserPoolClientOptions
        {
            GenerateSecret = true,
            OAuth = new OAuthSettings
            {
                Flows = new OAuthFlows { AuthorizationCodeGrant = true },
                Scopes =
                [
                    OAuthScope.ResourceServer(resourceServer, read),
                    OAuthScope.ResourceServer(resourceServer, write),
                    OAuthScope.ResourceServer(resourceServer, approve),
                ],
                CallbackUrls = ["https://example.com/placeholder"],
            },
        });

        // AgentCore Identity's credential provider for this user pool. Its
        // callback URL is an attribute of the created provider, and the
        // client's callback list cannot reference it without a circular
        // dependency, so the deploy step patches the client afterwards
        var credentialProvider = OAuth2CredentialProvider.UsingCognito(this, "CognitoProvider",
            new IncludedOauth2TenantCredentialProviderProps
            {
                OAuth2CredentialProviderName = "cognito-expenses",
                ClientId = agentClient.UserPoolClientId,
                ClientSecret = agentClient.UserPoolClientSecret,
                Issuer = $"https://cognito-idp.{Region}.amazonaws.com/{pool.UserPoolId}",
                AuthorizationEndpoint = $"{domain.BaseUrl()}/oauth2/authorize",
                TokenEndpoint = $"{domain.BaseUrl()}/oauth2/token",
            });

        // --- The expense MCP server: AOT zip on provided.al2023 ---
        var table = new Table(this, "Expenses", new TableProps
        {
            PartitionKey = new Attribute { Name = "pk", Type = AttributeType.STRING },
            SortKey = new Attribute { Name = "id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        var mcpServer = new Function(this, "McpServer", new FunctionProps
        {
            Runtime = LambdaRuntime.PROVIDED_AL2023,
            Architecture = Architecture.ARM_64,
            Handler = "bootstrap",
            Code = Code.FromAsset("artifacts/mcp-server"),
            MemorySize = 512,
            Timeout = Duration.Seconds(29),
            Environment = new Dictionary<string, string>
            {
                ["USER_POOL_ID"] = pool.UserPoolId,
                ["TABLE_NAME"] = table.TableName,
            },
        });
        table.GrantReadWriteData(mcpServer);

        var mcpUrl = mcpServer.AddFunctionUrl(new FunctionUrlOptions
        {
            AuthType = FunctionUrlAuthType.NONE,
        });

        // --- The agent on AgentCore Runtime, JWT inbound auth ---
        var agentRole = new Role(this, "AgentRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("bedrock-agentcore.amazonaws.com"),
        });
        agentRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream"],
            Resources = ["*"],
        }));
        agentRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["bedrock-agentcore:GetResourceOauth2Token"],
            Resources =
            [
                $"arn:aws:bedrock-agentcore:{Region}:{Account}:workload-identity-directory/default*",
                $"arn:aws:bedrock-agentcore:{Region}:{Account}:token-vault/default*",
            ],
        }));
        // AgentCore Identity keeps the provider's client secret in Secrets
        // Manager (the bang-scoped service-managed namespace) and reads it
        // with the caller's role during token exchange
        agentRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["secretsmanager:GetSecretValue"],
            Resources =
            [
                $"arn:aws:secretsmanager:{Region}:{Account}:secret:bedrock-agentcore-identity!default/oauth2/cognito-expenses-*",
            ],
        }));

        // The agent image is built and pushed by CI with buildx (OCI single
        // manifest; AgentCore rejects Docker v2 manifests at session start)
        var agentRepo = Repository.FromRepositoryName(this, "AgentRepo", "who-approved-this-agent");
        var agentTag = (string?)Node.TryGetContext("agentTag") ?? "latest";

        var runtime = new AgentRuntime(this, "AgentRuntime", new RuntimeProps
        {
            RuntimeName = "who_approved_this",
            AgentRuntimeArtifact = AgentRuntimeArtifact.FromEcrRepository(agentRepo, agentTag),
            NetworkConfiguration = RuntimeNetworkConfiguration.UsingPublicNetwork(),
            AuthorizerConfiguration = RuntimeAuthorizerConfiguration.UsingCognito(
                pool, [frontendClient]),
            ExecutionRole = agentRole,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["CREDENTIAL_PROVIDER"] = "cognito-expenses",
                ["MCP_SERVER_URL"] = mcpUrl.Url,
                ["MODEL_ID"] = "eu.amazon.nova-pro-v1:0",
                ["APP_URL"] = $"https://{distribution.DistributionDomainName}/",
            },
        });

        // --- Everything the deploy script and frontend config need ---
        new CfnOutput(this, "UserPoolId", new CfnOutputProps { Value = pool.UserPoolId });
        new CfnOutput(this, "HostedUiBase", new CfnOutputProps { Value = domain.BaseUrl() });
        new CfnOutput(this, "FrontendClientId", new CfnOutputProps { Value = frontendClient.UserPoolClientId });
        new CfnOutput(this, "AgentClientId", new CfnOutputProps { Value = agentClient.UserPoolClientId });
        new CfnOutput(this, "McpServerUrl", new CfnOutputProps { Value = mcpUrl.Url });
        new CfnOutput(this, "AgentRuntimeArn", new CfnOutputProps { Value = runtime.AgentRuntimeArn });
        new CfnOutput(this, "ProviderCallbackUrl", new CfnOutputProps { Value = credentialProvider.CallbackUrl! });
        new CfnOutput(this, "SiteBucket", new CfnOutputProps { Value = site.BucketName });
        new CfnOutput(this, "SiteUrl", new CfnOutputProps { Value = $"https://{distribution.DistributionDomainName}/" });
        new CfnOutput(this, "DistributionId", new CfnOutputProps { Value = distribution.DistributionId });
    }
}
