using System.Text.Json.Nodes;

namespace MainClient.Common
{
    public static class JsonNodeExtensions
    {
        public static JsonNode? SelectToken(this JsonNode? node, string path)
        {
            if (node is null || string.IsNullOrWhiteSpace(path))
                return node;

            var current = node;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current switch
                {
                    JsonObject obj => obj[segment],
                    JsonArray arr when int.TryParse(segment, out var index) && index >= 0 && index < arr.Count => arr[index],
                    _ => null
                };

                if (current is null)
                    return null;
            }

            return current;
        }
    }
}
