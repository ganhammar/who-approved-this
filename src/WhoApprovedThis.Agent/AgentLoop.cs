using System.Text.RegularExpressions;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using ModelContextProtocol.Client;
using McpTextBlock = ModelContextProtocol.Protocol.TextContentBlock;

namespace WhoApprovedThis.Agent;

public partial class AgentLoop
{
    static readonly string ModelId =
        Environment.GetEnvironmentVariable("MODEL_ID") ?? "eu.amazon.nova-pro-v1:0";
    static readonly string McpUrl =
        Environment.GetEnvironmentVariable("MCP_SERVER_URL") ?? "http://localhost:5199";

    const string SystemPrompt =
        "You are an expense assistant. Use the tools to list, submit, and " +
        "approve expenses for the user you are acting on behalf of. Relay " +
        "tool errors honestly: if the user is not allowed to do something, " +
        "say so plainly. Keep answers short.";

    readonly AmazonBedrockRuntimeClient _bedrock = new();

    // Without a delegated token the agent still answers; the model is told
    // to hand out the consent link only when the request actually needs it
    public async Task<string> RunWithoutAccess(string prompt, string authorizationUrl)
    {
        var response = await _bedrock.ConverseAsync(new ConverseRequest
        {
            ModelId = ModelId,
            System = [new() { Text =
                SystemPrompt +
                " You currently have NO access to the user's expenses. If the " +
                "request requires expense data or actions, ask the user to " +
                "grant you access first and include this link exactly as-is: " +
                authorizationUrl +
                " For everything else, answer normally and do not mention " +
                "access or the link." }],
            Messages =
                [new() { Role = ConversationRole.User, Content = [new() { Text = prompt }] }],
        });
        return Text(response);
    }

    public async Task<string> Run(string prompt, string userToken)
    {
        // The MCP connection carries the user-scoped token, so every tool
        // call downstream carries the caller's identity
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(McpUrl),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {userToken}",
            },
        }));

        var tools = await mcp.ListToolsAsync();
        var toolConfig = new ToolConfiguration
        {
            Tools = [.. tools.Select(tool => new Tool
            {
                ToolSpec = new ToolSpecification
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = new ToolInputSchema
                    {
                        Json = tool.JsonSchema.ToDocument(),
                    },
                },
            })],
        };

        List<Message> messages =
            [new() { Role = ConversationRole.User, Content = [new() { Text = prompt }] }];

        for (var turn = 0; turn < 8; turn++)
        {
            var response = await _bedrock.ConverseAsync(new ConverseRequest
            {
                ModelId = ModelId,
                System = [new() { Text = SystemPrompt }],
                Messages = messages,
                ToolConfig = toolConfig,
            });

            messages.Add(response.Output.Message);
            if (response.StopReason != StopReason.Tool_use)
                return Text(response);

            var results = new List<ContentBlock>();
            foreach (var use in response.Output.Message.Content
                .Select(block => block.ToolUse)
                .Where(use => use is not null))
            {
                var result = await mcp.CallToolAsync(
                    use!.Name, use.Input.ToArguments());
                results.Add(new()
                {
                    ToolResult = new ToolResultBlock
                    {
                        ToolUseId = use.ToolUseId,
                        Status = result.IsError is true
                            ? ToolResultStatus.Error
                            : ToolResultStatus.Success,
                        Content = [new()
                        {
                            Text = string.Join("\n", result.Content
                                .OfType<McpTextBlock>()
                                .Select(block => block.Text)),
                        }],
                    },
                });
            }
            messages.Add(new() { Role = ConversationRole.User, Content = results });
        }

        return "I could not finish within the allowed number of steps.";
    }

    static string Text(ConverseResponse response) =>
        ThinkingBlocks().Replace(
            string.Concat(response.Output.Message.Content
                .Where(block => block.Text is not null)
                .Select(block => block.Text)),
            "").Trim();

    // Amazon Nova models interleave <thinking> blocks into their text output
    [GeneratedRegex(@"<thinking>.*?</thinking>\s*", RegexOptions.Singleline)]
    private static partial Regex ThinkingBlocks();
}
