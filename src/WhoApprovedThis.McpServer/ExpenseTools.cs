using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WhoApprovedThis.McpServer;

public record Expense(
    string Id, string SubmittedBy, string Description,
    decimal Amount, string Status, string? ApprovedBy);

public record CompleteAuthRequest(string SessionId);

[JsonSerializable(typeof(Expense))]
[JsonSerializable(typeof(List<Expense>))]
[JsonSerializable(typeof(CompleteAuthRequest))]
public partial class AppJsonContext : JsonSerializerContext;

[McpServerToolType]
public class ExpenseTools(ExpenseStore store, IHttpContextAccessor http)
{
    ClaimsPrincipal User => http.HttpContext!.User;

    [McpServerTool(Name = "list_expenses")]
    [Description("List expenses. Employees see their own, managers see all.")]
    public async Task<List<Expense>> ListExpenses()
    {
        RequireScope("expenses/read");
        var expenses = await store.All();
        return IsManager()
            ? expenses
            : [.. expenses.Where(e => e.SubmittedBy == User.Identity!.Name)];
    }

    [McpServerTool(Name = "submit_expense")]
    [Description("Submit a new expense for the current user.")]
    public async Task<Expense> SubmitExpense(
        [Description("Short description of the expense")] string description,
        [Description("Amount in SEK")] decimal amount)
    {
        RequireScope("expenses/write");
        // Version 7 ids lead with a timestamp, and id is the table's sort
        // key, so expenses come back in submission order
        var expense = new Expense(
            $"exp-{Guid.CreateVersion7():N}"[..16], User.Identity!.Name!,
            description, amount, "pending", null);
        await store.Put(expense);
        return expense;
    }

    [McpServerTool(Name = "approve_expense")]
    [Description("Approve a pending expense. Managers only.")]
    public async Task<Expense> ApproveExpense([Description("The expense id")] string id)
    {
        RequireScope("expenses/approve");
        RequireGroup("managers");
        var expense = await store.Get(id)
            ?? throw new McpException($"No expense with id '{id}'.");
        var approved = expense with
        {
            Status = "approved",
            ApprovedBy = User.Identity!.Name,
        };
        await store.Put(approved);
        return approved;
    }

    // Scope says what this client was allowed to ask for on the user's
    // behalf; the group claim says what the user actually is. Approving
    // requires both.
    void RequireScope(string scope)
    {
        var granted = User.FindFirstValue("scope")?.Split(' ') ?? [];
        if (!granted.Contains(scope))
            throw new McpException($"Token is missing required scope '{scope}'.");
    }

    void RequireGroup(string group)
    {
        if (!User.HasClaim("cognito:groups", group))
            throw new McpException($"'{User.Identity!.Name}' is not in group '{group}'.");
    }

    bool IsManager() => User.HasClaim("cognito:groups", "managers");
}
