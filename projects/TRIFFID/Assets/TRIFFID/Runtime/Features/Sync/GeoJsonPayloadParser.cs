using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class GeoJsonPayloadParser
{
    public static Root Parse(string json, out int droppedMissingClass)
    {
        droppedMissingClass = 0;
        if (string.IsNullOrWhiteSpace(json)) return null;
        JObject root = JsonConvert.DeserializeObject<JObject>(json,
            new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
        if (root == null) return null;
        JArray features = root["features"] as JArray;
        if (features == null)
            throw new JsonSerializationException("Expected a complete GeoJSON features array. Local data retained.");
        foreach (JToken token in features)
        {
            JObject feature = token as JObject;
            if (feature == null) throw new JsonSerializationException("Remote collection contains a malformed feature.");
            JToken id = feature["id"];
            if (id == null || id.Type == JTokenType.Null || string.IsNullOrWhiteSpace(id.ToString()))
                throw new JsonSerializationException("Remote feature has no identifier. Local data retained.");
        }
        // Display fallbacks and class lookup must never rewrite the wire document.
        return root.ToObject<Root>();
    }
}
