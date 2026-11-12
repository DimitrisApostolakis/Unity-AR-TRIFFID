using System;
using UnityEngine;

// Shows a card for every alert UgvSensorMonitor finds to be newer than the
// last one seen. Alerts that arrive together are shown one after the other,
// oldest first; TriffidNotification keeps them in line. When more arrive than
// UgvSensorSettings allows per check, only the newest are shown and the last
// card says how many were left out. Whether the cards are quiet, and their
// colour, also come from UgvSensorSettings.
//
//   TEMPERATURE ALERT
//   31.5 °C, limit 29.0 °C
//   11:28 UTC · 41.81262, 2.10069
public static class UgvSensorAlertNotice
{
    // After the monitor has cleared its events, before it can raise one.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install()
    {
        UgvSensorMonitor.AlertsArrived -= OnAlertsArrived;
        UgvSensorMonitor.AlertsArrived += OnAlertsArrived;
    }

    private static void OnAlertsArrived(UgvSensorAlertBatch batch)
    {
        UgvSensorSettings settings = UgvSensorSettings.Current;
        if (!settings.showAlertNotifications || batch == null || batch.alerts.Count == 0)
            return;

        int total = batch.alerts.Count;
        int shown = Mathf.Min(total, Mathf.Max(1, settings.maxAlertCardsPerCheck));

        for (int i = total - shown; i < total; i++)
        {
            UgvSensorAlert alert = batch.alerts[i];
            UgvSensorDefinition definition = settings.Find(alert.sensor);

            string title = UgvSensorText.Name(definition, alert.sensor) + " alert";
            string message = UgvSensorText.Value(definition, alert.value) + ", limit " + UgvSensorText.Value(definition, alert.threshold) + "\n" + Where(alert);
            if (i == total - 1 && shown < total)
                message += " \u00B7 +" + (total - shown) + " more";

            TriffidNotification.Show(settings.alertNotificationKind, message, title, settings.quietAlertNotifications);
        }
    }

    private static string Where(UgvSensorAlert alert)
    {
        string place = UgvSensorText.Location(alert);
        return alert.hasTimestamp ? UgvSensorText.Time(alert.timestampUtc, DateTime.UtcNow) + " · " + place : place;
    }
}
