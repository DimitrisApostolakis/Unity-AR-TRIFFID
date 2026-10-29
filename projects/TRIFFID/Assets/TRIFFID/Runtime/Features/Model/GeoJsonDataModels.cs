using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;

public class Properties
{
    [JsonProperty("class")] public string className;
    public string id;
    public float? confidence;
    public string category;
    public string source;
    public float? altitude_m;

    [JsonProperty("height_above_surface_m", NullValueHandling = NullValueHandling.Ignore)]
    public float? height_above_surface_m;

    [JsonProperty("heights_above_surface_m", NullValueHandling = NullValueHandling.Ignore)]
    public JToken heights_above_surface_m;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string heightSource;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string heightStatus;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JToken heightSources;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public JToken heightStatuses;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string elevationStatus;
    [JsonProperty("ar_client_id", NullValueHandling = NullValueHandling.Ignore)] public string clientFeatureId;

    [JsonProperty("marker-color", NullValueHandling = NullValueHandling.Ignore)]
    public string marker_color;

    [JsonProperty("fill", NullValueHandling = NullValueHandling.Ignore)]
    public string fill;

    [JsonProperty("fill-opacity", NullValueHandling = NullValueHandling.Ignore)]
    public float? fill_opacity;
}

public class Geometry
{
    public string type;
    public JToken coordinates;

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public List<Geometry> geometries;
}

[JsonConverter(typeof(GeoJsonFeatureConverter))]
public class Feature
{
    public string type = "Feature";
    public string id;
    public Properties properties;
    public Geometry geometry;

    // Keep the source representation, including foreign members, absent fields,
    // numeric IDs and property precision. Only explicit field changes replace it.
    [JsonIgnore] internal JObject originalJson;
    [JsonIgnore] internal JObject originalFields;
}

public sealed class GeoJsonFeatureConverter : JsonConverter
{
    private sealed class FieldResolver : DefaultContractResolver
    {
        protected override JsonConverter ResolveContractConverter(Type type)
        { return type == typeof(Feature) ? null : base.ResolveContractConverter(type); }
    }

    private static readonly FieldResolver Fields = new FieldResolver();

    internal static JObject FieldSnapshot(Feature feature)
    { return JObject.FromObject(feature, JsonSerializer.Create(new JsonSerializerSettings { ContractResolver = Fields })); }

    public override bool CanConvert(Type type) { return type == typeof(Feature); }

    public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        // Dates in foreign properties are data, not instructions to reformat them.
        DateParseHandling dates = reader.DateParseHandling;
        JObject source;
        try { reader.DateParseHandling = DateParseHandling.None; source = JObject.Load(reader); }
        finally { reader.DateParseHandling = dates; }
        Feature feature = new Feature();
        JObject display = (JObject)source.DeepClone();
        JObject properties = display["properties"] as JObject;
        // Read previously saved identities without rewriting original features.
        // New features serialize only the neutral ar_client_id name.
        if (properties != null && properties["ar_client_id"] == null && properties["ar2_client_id"] != null)
            properties["ar_client_id"] = properties["ar2_client_id"].DeepClone();
        if (properties != null)
            foreach (string name in new[] { "confidence", "altitude_m", "height_above_surface_m", "fill-opacity" })
            {
                JToken token = properties[name];
                if (token == null || token.Type == JTokenType.Null) continue;
                double number;
                if (double.TryParse(token.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out number) &&
                    !double.IsNaN(number) && !double.IsInfinity(number) && Math.Abs(number) <= float.MaxValue)
                    properties[name] = number;
                else properties.Remove(name);
            }
        using (JsonReader input = display.CreateReader()) serializer.Populate(input, feature);
        feature.originalJson = source;
        feature.originalFields = FieldSnapshot(feature);
        return feature;
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        Feature feature = (Feature)value;
        JObject current = FieldSnapshot(feature);
        JToken output = feature.originalJson == null ? current :
            ApplyChanges(feature.originalJson, feature.originalFields, current);
        output.WriteTo(writer);
    }

    private static JToken ApplyChanges(JToken original, JToken before, JToken after)
    {
        if (JToken.DeepEquals(before, after)) return original == null ? null : original.DeepClone();
        JObject oldFields = before as JObject, newFields = after as JObject;
        if (oldFields != null && newFields != null)
        {
            JObject result = original is JObject ? (JObject)original.DeepClone() : new JObject();
            foreach (JProperty property in oldFields.Properties())
                if (newFields.Property(property.Name) == null) result.Remove(property.Name);
            foreach (JProperty property in newFields.Properties())
            {
                JToken previous = oldFields[property.Name];
                if (JToken.DeepEquals(previous, property.Value)) continue;
                result[property.Name] = ApplyChanges(result[property.Name], previous, property.Value);
            }
            return result;
        }
        JArray oldArray = before as JArray, newArray = after as JArray, sourceArray = original as JArray;
        if (oldArray != null && newArray != null && sourceArray != null &&
            oldArray.Count == newArray.Count && sourceArray.Count == newArray.Count)
        {
            JArray result = new JArray();
            for (int i = 0; i < newArray.Count; i++) result.Add(ApplyChanges(sourceArray[i], oldArray[i], newArray[i]));
            return result;
        }
        return after == null ? null : after.DeepClone();
    }
}

public class Root
{
    [JsonExtensionData] public IDictionary<string, JToken> extraMembers;
    [JsonProperty("type", Order = -2)]
    public string type = "FeatureCollection";

    [JsonProperty("lastModified", Order = -1, NullValueHandling = NullValueHandling.Ignore)]
    public string lastModified;

    public List<Feature> features;

    // Local outbox and tombstones are committed in the same file as the features.
    // Only individual Feature payloads are sent to the REST service.
    [JsonProperty("_ar2Sync", NullValueHandling = NullValueHandling.Ignore)]
    public GeoJsonSyncState syncState;
}

[Serializable]
public class PrefabEntry
{
    public string name;
    public GameObject prefab;
}

public class TransformConfig
{
    public OriginWgs84 origin_wgs84;
    public ColmapToEnu colmap_to_enu;
}

public class OriginWgs84
{
    public double lat;
    public double lon;
    public double alt;
}

public class ColmapToEnu
{
    public float scale;
    public float[] R_rowmajor;
    public float[] t;
}

[Serializable]
public class MqttLatestStamp
{
    public int sec;
    public int nanosec;
}

[Serializable]
public class MqttLatestHeader
{
    public MqttLatestStamp stamp;

    [JsonProperty("frame_id")]
    public string frame_id;
}

[Serializable]
public class MqttLatestMessage
{
    public MqttLatestHeader header;
    public double? roll;
    public double? pitch;
    public double? yaw;
    public double? latitude;
    public double? longitude;
    public double? battery_temperature;
    public double? power_voltage;
    public double? power_current;
    public double? temperature_ntc1;
    public double? temperature_ntc2;
    public int? mode;
    public string commander;
    public string behavior;
    public string action_command;
}

[Serializable]
public class MqttLatestResponse
{
    public MqttLatestMessage message;
    public string topic;
    public string time;
}


