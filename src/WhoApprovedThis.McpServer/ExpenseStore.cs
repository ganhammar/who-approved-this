using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace WhoApprovedThis.McpServer;

// Low-level DynamoDB client only: the Document and DataModel layers rely on
// reflection and do not work under Native AOT.
public class ExpenseStore(IAmazonDynamoDB db)
{
    static readonly string Table =
        Environment.GetEnvironmentVariable("TABLE_NAME") ?? "who-approved-this";

    public async Task<List<Expense>> All() =>
        [.. (await db.QueryAsync(new QueryRequest
        {
            TableName = Table,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = new() { [":pk"] = new("EXPENSE") },
        })).Items.Select(FromItem)];

    public async Task<Expense?> Get(string id)
    {
        var response = await db.GetItemAsync(Table, Key(id));
        return response.IsItemSet ? FromItem(response.Item) : null;
    }

    public async Task Put(Expense expense) =>
        await db.PutItemAsync(Table, ToItem(expense));

    static Dictionary<string, AttributeValue> Key(string id) =>
        new() { ["pk"] = new("EXPENSE"), ["id"] = new(id) };

    static Dictionary<string, AttributeValue> ToItem(Expense e)
    {
        var item = Key(e.Id);
        item["submittedBy"] = new(e.SubmittedBy);
        item["description"] = new(e.Description);
        item["amount"] = new() { N = e.Amount.ToString(CultureInfo.InvariantCulture) };
        item["status"] = new(e.Status);
        if (e.ApprovedBy is not null) item["approvedBy"] = new(e.ApprovedBy);
        return item;
    }

    static Expense FromItem(Dictionary<string, AttributeValue> item) => new(
        item["id"].S, item["submittedBy"].S, item["description"].S,
        decimal.Parse(item["amount"].N, CultureInfo.InvariantCulture),
        item["status"].S, item.GetValueOrDefault("approvedBy")?.S);
}
