using System;
using System.Collections;
using UnityEngine.Networking;

// The HTTP side of the UGV readings: builds the addresses and performs the
// GET requests. It keeps no state; UgvSensorMonitor decides when to ask.
public static class UgvSensorApi
{
    // done(reachable, body): body is null unless the server answered with
    // success. reachable is false only when no answer arrived at all.
    public static IEnumerator Get(string url, int timeoutSeconds, Action<bool, string> done)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = Math.Max(1, timeoutSeconds);
            yield return request.SendWebRequest();

            bool reachable = request.result != UnityWebRequest.Result.ConnectionError;
            bool success = request.result == UnityWebRequest.Result.Success;
            done(reachable, success ? request.downloadHandler.text : null);
        }
    }

    // sensorKey null or empty: alerts of every reading.
    public static string AlertsUrl(string baseUrl, int limit, string sensorKey = null)
    {
        string url = AddQuery(baseUrl, "limit=" + Math.Max(1, limit));
        if (!string.IsNullOrEmpty(sensorKey))
            url = AddQuery(url, "sensor=" + UnityWebRequest.EscapeURL(sensorKey));
        return url;
    }

    private static string AddQuery(string url, string query)
    {
        return url + (url.IndexOf('?') >= 0 ? "&" : "?") + query;
    }
}
