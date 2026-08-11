using System.Text.Json.Serialization;
using WhoApprovedThis.Agent;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AgentJsonContext.Default));
builder.Services.AddSingleton<TokenBroker>();
builder.Services.AddSingleton<AgentLoop>();
builder.WebHost.UseUrls("http://0.0.0.0:8080");

var app = builder.Build();

app.MapGet("/ping", () => new PingResponse("Healthy"));

app.MapPost("/invocations", async (
    InvocationRequest payload, HttpRequest request, TokenBroker broker, AgentLoop agent) =>
{
    if (payload.Prompt is not { Length: > 0 } prompt)
        return Results.BadRequest();

    // The runtime has already validated the caller's JWT and delivered a
    // workload access token bound to that user in this header
    var workloadToken = request.Headers["WorkloadAccessToken"].ToString();

    var (userToken, authorizationUrl) = await broker.GetUserToken(workloadToken);
    if (authorizationUrl is not null)
        return Results.Ok(new InvocationResponse(
            "Before I can act on your behalf, you need to grant me access: " +
            authorizationUrl));

    var answer = await agent.Run(prompt, userToken!);
    return Results.Ok(new InvocationResponse(answer));
});

app.Run();

public record PingResponse(string Status);
public record InvocationRequest(string Prompt);
public record InvocationResponse(string Message);

[JsonSerializable(typeof(PingResponse))]
[JsonSerializable(typeof(InvocationRequest))]
[JsonSerializable(typeof(InvocationResponse))]
public partial class AgentJsonContext : JsonSerializerContext;
