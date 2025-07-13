using ConfluenceSyncService.Services.Clients;

public class TestConfluenceFetch
{
    private readonly ConfluenceClient _client;

    public TestConfluenceFetch(ConfluenceClient client)
    {
        _client = client;
    }

    public async Task RunAsync(string databaseId)
    {
        var entries = await _client.GetDatabaseEntriesAsync(databaseId);
        Console.WriteLine($"Retrieved {entries.Count} entries from Confluence:");

        foreach (var entry in entries)
        {
            Console.WriteLine($"- ID: {entry.Id}, Modified: {entry.LastModifiedUtc:u}");
            foreach (var field in entry.Fields)
            {
                Console.WriteLine($"    {field.Key}: {field.Value}");
            }
        }
    }
}
