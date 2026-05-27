using System.Text.Json;
using System.Text.Json.Nodes;


namespace MainClient.Extensions;


internal static class JsonNodeExtensions
{
    public static T? Value<T>(this JsonNode? node)
    {
        if (node is null)
        {
            return default;
        }

        try
        {
            return node.Deserialize<T>();
        }
        catch
        {
            return default;
        }
    }


    public static IEnumerable<JsonNode> Children(this JsonNode? node)
    {
        if (node is JsonArray arr)
        {
            return arr.Where(x => x is not null).Select(x => x!);
        }

        if (node is JsonObject obj)
        {
            return obj.Select(x => x.Value).Where(x => x is not null).Select(x => x!);
        }

        return Enumerable.Empty<JsonNode>();
    }

    public static JsonNode? SelectToken(this JsonNode? node, string path)
    {
        if (node is null || string.IsNullOrWhiteSpace(path))
        {
            return node;
        }

        var current = node;
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
            {
                return null;
            }

            if (current is JsonArray arr && int.TryParse(raw, out var idx))
            {
                current = idx >= 0 && idx < arr.Count ? arr[idx] : null;
                continue;
            }

            if (current is JsonObject obj)
            {
                current = obj[raw];
                continue;
            }

            return null;
        }

        return current;
    }
}
