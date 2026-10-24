using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class GeoJsonPayloadParser
{
    public static Root Parse(string json, out int droppedMissingClass)
    {
        droppedMissingClass = 0;

        if (string.IsNullOrWhiteSpace(json))
            return null;

        JToken token = JToken.Parse(json);
        if (!(token is JObject rootObj))
            return null;

        if (!(rootObj["features"] is JArray features))
            throw new JsonSerializationException("Expected a complete GeoJSON features array. Local data retained.");

        JArray normalizedFeatures = new JArray();
        foreach (JToken featureToken in features)
        {
            if (!(featureToken is JObject featureObj))
                throw new JsonSerializationException("Remote collection contains a malformed feature.");

            JObject propertiesObj = featureObj["properties"] as JObject;
            if (propertiesObj == null)
            {
                propertiesObj = new JObject();
                featureObj["properties"] = propertiesObj;
            }

            string className = propertiesObj["class"]?.Type == JTokenType.Null
                ? string.Empty
                : (propertiesObj["class"]?.ToString() ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(className))
            {
                droppedMissingClass++;
                throw new JsonSerializationException("Remote feature has no class. An incomplete collection cannot replace local data.");
            }

            propertiesObj["class"] = className;

            NormalizeStringField(propertiesObj, "id");
            NormalizeStringField(propertiesObj, "category");
            NormalizeStringField(propertiesObj, "source");
            NormalizeStringField(propertiesObj, "marker-color");

            NormalizeNumberField(propertiesObj, "confidence", 0d);
            NormalizeOptionalNumberField(propertiesObj, "altitude_m");
            NormalizeOptionalNumberField(propertiesObj, "height_above_surface_m");

            JToken featureId = featureObj["id"];
            if (featureId == null || featureId.Type == JTokenType.Null || string.IsNullOrWhiteSpace(featureId.ToString()))
                throw new JsonSerializationException("Remote feature has no identifier. Local data retained.");

            featureObj["id"] = featureId.ToString().Trim();

            normalizedFeatures.Add(featureObj);
        }

        rootObj["features"] = normalizedFeatures;
        return rootObj.ToObject<Root>();
    }

    private static void NormalizeStringField(JObject obj, string fieldName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(fieldName))
            return;

        JToken token = obj[fieldName];
        if (token == null || token.Type == JTokenType.Null)
        {
            obj[fieldName] = string.Empty;
            return;
        }

        obj[fieldName] = token.ToString().Trim();
    }

    private static void NormalizeNumberField(JObject obj, string fieldName, double fallback)
    {
        if (obj == null || string.IsNullOrWhiteSpace(fieldName))
            return;

        JToken token = obj[fieldName];
        if (token == null || token.Type == JTokenType.Null)
        {
            obj[fieldName] = fallback;
            return;
        }

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            return;

        string raw = token.ToString().Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            obj[fieldName] = parsed;
        else
            obj[fieldName] = fallback;
    }

    private static void NormalizeOptionalNumberField(JObject obj, string fieldName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(fieldName))
            return;

        JToken token = obj[fieldName];
        if (token == null || token.Type == JTokenType.Null)
            return;

        double parsed;
        if (double.TryParse(token.ToString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
            !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            obj[fieldName] = parsed;
        else
            obj.Remove(fieldName);
    }
}
