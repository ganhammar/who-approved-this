using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using ModelContextProtocol.Client;
using McpTextBlock = ModelContextProtocol.Protocol.TextContentBlock;

namespace WhoApprovedThis.Agent;

public class AgentLoop
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

    public async Task<string> Run(string prompt, string userToken)
    {
        // The MCP connection carries the user-scoped token: every tool call
        // downstream executes as the caller, not as the agent
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
                return string.Concat(response.Output.Message.Content
                    .Where(block => block.Text is not null)
                    .Select(block => block.Text));

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
}
