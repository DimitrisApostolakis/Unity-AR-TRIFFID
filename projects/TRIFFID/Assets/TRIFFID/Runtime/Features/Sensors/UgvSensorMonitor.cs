using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Keeps the UGV environmental readings up to date and reports new alerts.
//
// It creates itself when a Play session starts (unless switched off in
// UgvSensorSettings), asks the API at a fixed pace and holds one
// UgvSensorState per configured reading. It shows nothing itself:
//
//   UgvSensorMonitor.Changed        the limits or a last alert changed
//   UgvSensorMonitor.AlertsArrived  alerts newer than the last one seen
//
// "New" is decided by alert id. The highest id seen so far is stored on the
// device, once per alerts address (the real API and the local test server
// count separately), so the comparison also holds across restarts: an alert recorded
// while the app was closed is reported at the next start, and an alert that
// was already reported is never reported twice.
[DisallowMultipleComponent]
public sealed class UgvSensorMonitor : MonoBehaviour
{
    private const string LastSeenIdKeyPrefix = "TRIFFID.UgvSensors.LastSeenAlertId.";
    private const long NoAlertSeen = -1;

    public static event Action Changed;
    public static event Action<UgvSensorAlertBatch> AlertsArrived;

    public static UgvSensorMonitor Instance { get; private set; }

    private readonly List<UgvSensorState> states = new List<UgvSensorState>();
    private readonly Dictionary<string, UgvSensorState> statesByKey = new Dictionary<string, UgvSensorState>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> thresholdBuffer = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    private UgvSensorSettings settings;
    private long lastSeenAlertId = NoAlertSeen;
    private bool historyLoaded;
    private bool reachable = true;
    private float nextThresholdRefresh;

    public IReadOnlyList<UgvSensorState> Sensors { get { return states; } }

    // True once the API has answered; until then every reading is Unknown.
    public bool DataReceived { get; private set; }

    // Id of the newest alert seen so far, or -1 when none was ever seen.
    public long LastSeenAlertId { get { return lastSeenAlertId; } }

    public UgvSensorState StateOf(string key)
    {
        UgvSensorState state;
        return key != null && statesByKey.TryGetValue(key, out state) ? state : null;
    }

    // Play sessions in the Editor can keep statics alive; start each one fresh.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Changed = null;
        AlertsArrived = null;
        Instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (Instance != null || !UgvSensorSettings.Current.sensorsEnabled)
            return;

        GameObject host = new GameObject("UGV Sensor Monitor");
        DontDestroyOnLoad(host);
        host.AddComponent<UgvSensorMonitor>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        settings = UgvSensorSettings.Current;

        foreach (UgvSensorDefinition definition in settings.sensors)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.key) || statesByKey.ContainsKey(definition.key))
                continue;

            UgvSensorState state = new UgvSensorState(definition);
            states.Add(state);
            statesByKey[definition.key] = state;
        }

        lastSeenAlertId = ReadLastSeenId();
    }

    private void OnEnable()
    {
        if (Instance == this)
            StartCoroutine(Run());
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private IEnumerator Run()
    {
        WaitForSecondsRealtime wait = new WaitForSecondsRealtime(Mathf.Max(1f, settings.pollSeconds));

        while (true)
        {
            if (Time.realtimeSinceStartup >= nextThresholdRefresh)
                yield return RefreshThresholds();

            // The general list holds only the newest alerts, so a reading
            // whose last alert is older is asked for once on its own.
            if (!historyLoaded)
                yield return LoadHistory();

            yield return CheckForNewAlerts();
            yield return wait;
        }
    }

    private IEnumerator RefreshThresholds()
    {
        bool answered = false;
        string body = null;
        yield return UgvSensorApi.Get(settings.ThresholdsUrl, settings.requestTimeoutSeconds, (ok, text) => { answered = ok; body = text; });
        ReportReachable(answered);

        thresholdBuffer.Clear();
        if (body == null || !UgvSensorPayloadParser.TryParseThresholds(body, thresholdBuffer))
            yield break;

        nextThresholdRefresh = Time.realtimeSinceStartup + settings.thresholdRefreshSeconds;
        DataReceived = true;
        ApplyThresholds(thresholdBuffer);
        RaiseChanged();
    }

    private IEnumerator LoadHistory()
    {
        bool complete = true;
        bool changed = false;

        foreach (UgvSensorState state in states)
        {
            string body = null;
            string url = UgvSensorApi.AlertsUrl(settings.AlertsUrl, 1, state.definition.key);
            yield return UgvSensorApi.Get(url, settings.requestTimeoutSeconds, (ok, text) => body = text);

            UgvSensorAlertPage page;
            if (body == null || !UgvSensorPayloadParser.TryParseAlerts(body, out page))
            {
                complete = false;
                continue;
            }

            foreach (UgvSensorAlert alert in page.alerts)
                changed |= Remember(alert);
        }

        historyLoaded = complete;
        if (changed)
            RaiseChanged();
    }

    private IEnumerator CheckForNewAlerts()
    {
        bool answered = false;
        string body = null;
        string url = UgvSensorApi.AlertsUrl(settings.AlertsUrl, settings.alertsPerPoll);
        yield return UgvSensorApi.Get(url, settings.requestTimeoutSeconds, (ok, text) => { answered = ok; body = text; });
        ReportReachable(answered);

        UgvSensorAlertPage page;
        if (body == null || !UgvSensorPayloadParser.TryParseAlerts(body, out page))
            yield break;

        bool changed = !DataReceived;
        DataReceived = true;
        changed |= ApplyThresholds(page.thresholds);

        foreach (UgvSensorAlert alert in page.alerts)
            changed |= Remember(alert);

        if (changed)
            RaiseChanged();

        ReportNewAlerts(page.alerts);
    }

    // Compares the ids of the received alerts with the newest id seen before.
    private void ReportNewAlerts(List<UgvSensorAlert> alerts)
    {
        UgvSensorAlert latest = null;
        List<UgvSensorAlert> newer = new List<UgvSensorAlert>();

        // The list is ordered by time, not by id, so every entry is looked at.
        foreach (UgvSensorAlert alert in alerts)
        {
            if (!statesByKey.ContainsKey(alert.sensor))
                continue;
            if (latest == null || alert.id > latest.id)
                latest = alert;
            if (alert.id > lastSeenAlertId)
                newer.Add(alert);
        }

        if (latest == null || latest.id == lastSeenAlertId)
            return;

        long previousId = lastSeenAlertId;
        lastSeenAlertId = latest.id;
        WriteLastSeenId(lastSeenAlertId);

        // Ids below the remembered one: the server list was cleared and
        // counts again from the start. Follow it without announcing anything.
        if (latest.id < previousId)
        {
            TriffidLog.Info("UgvSensorMonitor", "Newest alert id went from " + previousId + " back to " + latest.id + "; following the server.", this);
            return;
        }

        bool firstRun = previousId == NoAlertSeen;
        if (firstRun && !settings.announceLatestOnFirstRun)
            return;

        TriffidLog.Info("UgvSensorMonitor", firstRun
            ? "Latest alert is " + latest.id + " (" + latest.sensor + "); no earlier alert was known."
            : "New alert " + latest.id + " (" + latest.sensor + "); the previous alert was " + previousId + ".", this);

        Action<UgvSensorAlertBatch> handler = AlertsArrived;
        if (handler == null)
            return;

        // Without an earlier alert to compare with, only the latest one is news.
        UgvSensorAlertBatch batch = new UgvSensorAlertBatch { previousId = previousId };
        if (firstRun)
            batch.alerts.Add(latest);
        else
        {
            newer.Sort((a, b) => a.id.CompareTo(b.id));
            batch.alerts.AddRange(newer);
        }
        handler(batch);
    }

    private bool ApplyThresholds(Dictionary<string, double> thresholds)
    {
        bool changed = false;
        foreach (UgvSensorState state in states)
        {
            double limit;
            if (!thresholds.TryGetValue(state.definition.key, out limit))
                continue;
            if (state.hasThreshold && state.threshold == limit)
                continue;

            state.hasThreshold = true;
            state.threshold = limit;
            changed = true;
        }
        return changed;
    }

    // Keeps the alert when it is the most recent one of its reading.
    private bool Remember(UgvSensorAlert alert)
    {
        UgvSensorState state = StateOf(alert.sensor);
        if (state == null)
            return false;
        if (state.lastAlert != null && (state.lastAlert.id == alert.id || !alert.IsNewerThan(state.lastAlert)))
            return false;

        state.lastAlert = alert;
        return true;
    }

    private void ReportReachable(bool answered)
    {
        if (answered == reachable)
            return;

        reachable = answered;
        if (answered)
            TriffidLog.Info("UgvSensorMonitor", "UGV sensor API is reachable again.", this);
        else
            TriffidLog.Warning("UgvSensorMonitor", "UGV sensor API did not answer; readings keep their last known state.", this);
    }

    private static void RaiseChanged()
    {
        Action handler = Changed;
        if (handler != null)
            handler();
    }

    // PlayerPrefs has no 64-bit type; a string keeps the full id.
    private string LastSeenIdKey
    {
        get { return LastSeenIdKeyPrefix + settings.AlertsUrl; }
    }

    private long ReadLastSeenId()
    {
        long id;
        return long.TryParse(PlayerPrefs.GetString(LastSeenIdKey, string.Empty), out id) ? id : NoAlertSeen;
    }

    private void WriteLastSeenId(long id)
    {
        PlayerPrefs.SetString(LastSeenIdKey, id.ToString());
        PlayerPrefs.Save();
    }

    // For testing: the next check then treats the latest alert as unseen.
    [ContextMenu("Forget Last Seen Alert")]
    private void ForgetLastSeenAlert()
    {
        PlayerPrefs.DeleteKey(LastSeenIdKey);
        PlayerPrefs.Save();
        lastSeenAlertId = NoAlertSeen;
        TriffidLog.Info("UgvSensorMonitor", "Last seen alert id was cleared.", this);
    }
}
