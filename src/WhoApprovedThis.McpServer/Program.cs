using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore.Authentication;
using WhoApprovedThis.McpServer;

var builder = WebApplication.CreateSlimBuilder(args);

var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? "eu-north-1";
var userPoolId = Environment.GetEnvironmentVariable("USER_POOL_ID") ?? "";
var issuer = $"https://cognito-idp.{region}.amazonaws.com/{userPoolId}";

builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi,
    new SourceGeneratorLambdaJsonSerializer<LambdaJsonContext>());

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.Authority = issuer;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            // Cognito access tokens carry the app client in client_id, not aud
            ValidateAudience = false,
            NameClaimType = "username",
        };
    })
    .AddMcp(options =>
    {
        // Served at /.well-known/oauth-protected-resource (RFC 9728), so MCP
        // clients can discover the authorization server and available scopes.
        // The resource identity derives from the request, so the server does
        // not need to know its own public URL
        options.Events.OnResourceMetadataRequest = context =>
        {
            context.ResourceMetadata = new()
            {
                Resource = $"{context.Request.Scheme}://{context.Request.Host}",
                AuthorizationServers = { issuer },
                ScopesSupported = ["expenses/read", "expenses/write", "expenses/approve"],
            };
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAmazonDynamoDB>(new AmazonDynamoDBClient());
builder.Services.AddSingleton<ExpenseStore>();

var jsonOptions = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
jsonOptions.TypeInfoResolverChain.Add(AppJsonContext.Default);

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<ExpenseTools>(jsonOptions);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapMcp().RequireAuthorization();

app.Run();

[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
[JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
public partial class LambdaJsonContext : JsonSerializerContext;
