using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class GeoJsonPendingEdit
{
    public string featureId;
    public string kind;
    public long revision;
    public string operationId;
    public Feature payload; // Also retains the last content of a deletion tombstone.
    public string baseHash;
    public string status = "pending";
    public int attempts;
    public long notBeforeUtcTicks;
    public bool mayHaveReachedServer;
    public string lastSubmittedHash;
    public string error;
    public Feature remoteConflict;
    public string remoteConflictId;
}

public sealed class GeoJsonSyncState
{
    public int schemaVersion = 1;
    public long nextRevision = 1;
    public Dictionary<string, string> remoteIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> acknowledgedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public List<GeoJsonPendingEdit> pending = new List<GeoJsonPendingEdit>();
}

// All state transitions operate on the same Root that JsonSpawner commits. There
// is no second queue file and no interval in which a saved deletion has no record.
public static class GeoJsonOutbox
{
    public static void Initialize(Root root, bool existingSnapshot)
    {
        if (root.features == null) root.features = new List<Feature>();
        if (root.syncState != null)
        {
            if (root.syncState.schemaVersion != 1 || root.syncState.pending == null ||
                root.syncState.remoteIds == null || root.syncState.acknowledgedHashes == null)
                throw new InvalidDataException("Unsupported or malformed local synchronization state. Original file retained.");
            return;
        }
        root.syncState = new GeoJsonSyncState();
        if (!existingSnapshot) return;
        // Loading is observation, not an edit. Retain a baseline without creating
        // operations. A later edit with no confirmed server ID must reconcile.
        foreach (Feature feature in root.features.Where(f => f != null && !string.IsNullOrWhiteSpace(f.id)))
        {
            root.syncState.acknowledgedHashes[feature.id] = Fingerprint(feature);
        }
    }

    public static Feature Clone(Feature feature)
    { return feature == null ? null : JsonConvert.DeserializeObject<Feature>(JsonConvert.SerializeObject(feature)); }

    public static string Fingerprint(Feature feature)
    {
        if (feature == null) return null;
        JObject value = JObject.FromObject(feature);
        value.Remove("id");
        JObject properties = value["properties"] as JObject;
        if (properties != null) { properties.Remove("id"); properties.Remove("ar_client_id"); properties.Remove("ar2_client_id"); }
        using (SHA256 hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToString(Formatting.None)))).Replace("-", "");
    }

    public static GeoJsonPendingEdit Find(Root root, string id)
    { return root.syncState.pending.Find(p => string.Equals(p.featureId, id, StringComparison.OrdinalIgnoreCase)); }

    private static GeoJsonPendingEdit NewEdit(GeoJsonSyncState state, string id, string kind, Feature payload, string baseHash)
    {
        return new GeoJsonPendingEdit { featureId = id, kind = kind, payload = Clone(payload), baseHash = baseHash,
            revision = state.nextRevision++, operationId = Guid.NewGuid().ToString("N") };
    }

    public static bool CaptureChanges(Root root, DateTime now, double debounceSeconds = 0d)
    {
        Initialize(root, false);
        GeoJsonSyncState state = root.syncState;
        bool changed = false;
        HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Feature feature in root.features)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.id)) continue;
            if (!live.Add(feature.id)) throw new InvalidDataException("Local collection has duplicate feature IDs: " + feature.id);
            GeoJsonPendingEdit previous = Find(root, feature.id);
            string baseline;
            state.acknowledgedHashes.TryGetValue(feature.id, out baseline);
            string currentHash = Fingerprint(feature);
            if (previous != null && previous.kind != "delete" && Fingerprint(previous.payload) == currentHash) continue;
            if (previous == null && baseline == currentHash) continue;

            string kind = state.remoteIds.ContainsKey(feature.id) ? "update" : "create";
            if (kind == "create" && baseline != null) kind = "reconcile";
            if (previous != null && previous.kind == "reconcile") { kind = "reconcile"; baseline = previous.baseHash; }
            if (kind == "create")
            {
                if (feature.properties == null) feature.properties = new Properties();
                if (string.IsNullOrEmpty(feature.properties.clientFeatureId)) feature.properties.clientFeatureId = feature.id;
            }
            GeoJsonPendingEdit next = NewEdit(state, feature.id, kind, feature, baseline);
            if (kind == "reconcile") next.status = "reconcile";
            next.notBeforeUtcTicks = now.AddSeconds(Math.Max(0d, debounceSeconds)).Ticks;
            PreserveUncertainty(previous, next);
            if (previous != null) state.pending.Remove(previous);
            state.pending.Add(next);
            changed = true;
        }
        HashSet<string> known = new HashSet<string>(state.acknowledgedHashes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (GeoJsonPendingEdit edit in state.pending) known.Add(edit.featureId);
        foreach (string id in known)
        {
            if (live.Contains(id)) continue;
            GeoJsonPendingEdit previous = Find(root, id);
            if (previous != null && previous.kind == "delete") continue;
            if (previous != null && previous.kind == "create" && !previous.mayHaveReachedServer && !state.remoteIds.ContainsKey(id))
            { state.pending.Remove(previous); changed = true; continue; }
            string baseline;
            state.acknowledgedHashes.TryGetValue(id, out baseline);
            GeoJsonPendingEdit deletion = NewEdit(state, id, "delete", previous == null ? null : previous.payload, baseline);
            PreserveUncertainty(previous, deletion);
            if (!state.remoteIds.ContainsKey(id)) deletion.status = "reconcile";
            if (previous != null) state.pending.Remove(previous);
            state.pending.Add(deletion);
            changed = true;
        }
        return changed;
    }

    private static void PreserveUncertainty(GeoJsonPendingEdit previous, GeoJsonPendingEdit next)
    {
        if (previous == null) return;
        next.mayHaveReachedServer = previous.mayHaveReachedServer;
        next.lastSubmittedHash = previous.lastSubmittedHash;
        if (previous.status == "conflict" || previous.status == "reconcile" ||
            (previous.kind == "create" && previous.mayHaveReachedServer))
        {
            next.status = previous.status == "conflict" ? "conflict" : "reconcile";
            next.remoteConflict = Clone(previous.remoteConflict);
            next.remoteConflictId = previous.remoteConflictId;
            next.error = previous.error;
        }
    }

    public static void RestoreAfterRestart(Root root)
    {
        Initialize(root, true);
        foreach (GeoJsonPendingEdit edit in root.syncState.pending)
        {
            if (edit.status != "inflight") continue;
            edit.status = edit.kind == "create" ? "reconcile" : "pending";
            edit.notBeforeUtcTicks = 0;
        }
    }

    public static void RecordDeletion(Root root, Feature feature)
    {
        Initialize(root, false);
        if (feature == null || string.IsNullOrWhiteSpace(feature.id)) return;
        GeoJsonPendingEdit previous = Find(root, feature.id);
        if (previous != null && previous.kind == "delete") return;
        if (previous != null && previous.kind == "create" && !previous.mayHaveReachedServer && !root.syncState.remoteIds.ContainsKey(feature.id))
        { root.syncState.pending.Remove(previous); return; }
        string baseline;
        root.syncState.acknowledgedHashes.TryGetValue(feature.id, out baseline);
        GeoJsonPendingEdit deletion = NewEdit(root.syncState, feature.id, "delete", feature, baseline);
        PreserveUncertainty(previous, deletion);
        if (!root.syncState.remoteIds.ContainsKey(feature.id)) deletion.status = "reconcile";
        if (previous != null) root.syncState.pending.Remove(previous);
        root.syncState.pending.Add(deletion);
    }

    public static bool RestoreDeletion(Root root, string id)
    {
        GeoJsonPendingEdit edit = Find(root, id);
        if (edit == null || edit.kind != "delete" || edit.payload == null) return false;
        root.features.Add(Clone(edit.payload));
        CaptureChanges(root, DateTime.UtcNow);
        return true;
    }

    public static GeoJsonPendingEdit Next(Root root, DateTime now)
    {
        foreach (GeoJsonPendingEdit edit in root.syncState.pending.OrderBy(p => p.revision))
        {
            if (edit.notBeforeUtcTicks > now.Ticks || edit.status == "conflict" || edit.status == "reconcile" || edit.status == "inflight") continue;
            if (edit.kind == "delete" && !root.syncState.remoteIds.ContainsKey(edit.featureId)) continue;
            // Return an immutable dispatch snapshot. A later local edit has its
            // own revision and must never be acknowledged by this request.
            return JsonConvert.DeserializeObject<GeoJsonPendingEdit>(JsonConvert.SerializeObject(edit));
        }
        return null;
    }

    public static void MarkDispatched(Root root, GeoJsonPendingEdit sent)
    {
        GeoJsonPendingEdit current = Find(root, sent.featureId);
        if (current == null || current.revision != sent.revision) throw new InvalidOperationException("Dispatch revision changed.");
        if (current.status == "failed") current.attempts = 0;
        current.status = "inflight";
        current.attempts++;
        current.mayHaveReachedServer = true;
        current.lastSubmittedHash = Fingerprint(sent.payload);
        sent.attempts = current.attempts;
        sent.lastSubmittedHash = current.lastSubmittedHash;
    }

    public static Feature NetworkPayload(Root root, GeoJsonPendingEdit sent)
    {
        Feature feature = Clone(sent.payload);
        if (feature == null) return null;
        string remoteId;
        if (root.syncState.remoteIds.TryGetValue(sent.featureId, out remoteId))
        {
            feature.id = remoteId;
            if (feature.properties != null) feature.properties.id = remoteId;
        }
        return feature;
    }

    public static void Acknowledge(Root root, GeoJsonPendingEdit sent, string remoteId)
    {
        GeoJsonSyncState state = root.syncState;
        if (sent.kind == "delete")
        { state.acknowledgedHashes.Remove(sent.featureId); state.remoteIds.Remove(sent.featureId); }
        else
        {
            if (!string.IsNullOrWhiteSpace(remoteId)) state.remoteIds[sent.featureId] = remoteId;
            state.acknowledgedHashes[sent.featureId] = Fingerprint(sent.payload);
        }
        GeoJsonPendingEdit current = Find(root, sent.featureId);
        if (current == null) return;
        if (current.status == "conflict") return;
        if (current.revision == sent.revision) state.pending.Remove(current);
        else
        {
            current.baseHash = sent.kind == "delete" ? null : Fingerprint(sent.payload);
            if (sent.kind == "create" && current.kind != "delete") current.kind = "update";
            if (sent.kind == "delete" && current.kind != "delete") current.kind = "create";
            current.status = "pending";
            current.attempts = 0;
            current.mayHaveReachedServer = false;
            current.notBeforeUtcTicks = 0;
        }
        CaptureChanges(root, DateTime.UtcNow);
    }

    public static void Fail(Root root, GeoJsonPendingEdit sent, string error, int maxRetries, double baseDelay, double cap, DateTime now)
    {
        GeoJsonPendingEdit current = Find(root, sent.featureId);
        if (current == null) return;
        if (current.revision != sent.revision)
        {
            if (sent.kind == "create") { current.status = "reconcile"; current.error = "Earlier create has an unconfirmed outcome."; }
            return;
        }
        current.error = error;
        if (sent.kind == "create")
        {
            current.status = "reconcile";
            current.error = "Create outcome unconfirmed. Server comparison is required before resending. " + error;
            return;
        }
        bool exhausted = current.attempts > Math.Max(0, maxRetries);
        current.status = exhausted ? "failed" : "pending";
        double delay = exhausted ? Math.Max(1d, cap) : Math.Min(Math.Max(0.1d, baseDelay) * Math.Pow(2d, Math.Min(30, current.attempts - 1)), Math.Max(baseDelay, cap));
        current.notBeforeUtcTicks = now.AddSeconds(Math.Max(0.1d, delay)).Ticks;
    }

    public static void Retry(Root root)
    {
        foreach (GeoJsonPendingEdit edit in root.syncState.pending)
        {
            if (edit.status == "conflict" || edit.status == "reconcile" || edit.status == "inflight") continue;
            edit.status = "pending"; edit.attempts = 0; edit.notBeforeUtcTicks = 0;
        }
    }

    public static bool MergeRemote(Root local, Root remote)
    {
        Initialize(local, true);
        if (remote == null || remote.features == null) throw new ArgumentException("A complete remote collection is required.");
        // Reject incomplete collections before changing any local feature. An
        // empty, valid collection means deletion; an invalid response does not.
        HashSet<string> remoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Feature feature in remote.features)
        {
            if (feature == null || string.IsNullOrWhiteSpace(feature.id))
                throw new InvalidDataException("Remote collection contains a feature without an identifier.");
            if (!remoteIds.Add(feature.id)) throw new InvalidOperationException("Remote collection has duplicate feature IDs.");
        }
        GeoJsonSyncState state = local.syncState;
        string before = JsonConvert.SerializeObject(local);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> clientCounts = remote.features.Where(f => f != null && f.properties != null && !string.IsNullOrWhiteSpace(f.properties.clientFeatureId))
            .GroupBy(f => f.properties.clientFeatureId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (Feature raw in remote.features)
        {
            string id = state.remoteIds.FirstOrDefault(kv => string.Equals(kv.Value, raw.id, StringComparison.OrdinalIgnoreCase)).Key;
            string clientId = raw.properties == null ? null : raw.properties.clientFeatureId;
            if (string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(clientId) && Find(local, clientId) != null) id = clientId;
            if (string.IsNullOrEmpty(id)) id = raw.id;
            seen.Add(id);
            GeoJsonPendingEdit pending = Find(local, id);
            Feature incoming = Clone(raw);
            // Rewrite identity only when binding a confirmed local alias.
            if (!string.Equals(id, raw.id, StringComparison.Ordinal))
            {
                incoming.id = id;
                if (incoming.properties != null) incoming.properties.id = id;
            }
            if (pending != null) pending.remoteConflictId = raw.id;
            if (pending != null && !string.IsNullOrEmpty(clientId) && clientCounts[clientId] > 1)
            { Conflict(pending, incoming, "Several server records share this client feature ID."); continue; }
            string remoteHash = Fingerprint(incoming);
            if (pending != null)
            {
                if (pending.status == "conflict")
                {
                    // Migrate conflicts created by the former keep-local policy.
                    // Unconfirmed creates still need identity reconciliation.
                    if (state.remoteIds.ContainsKey(id) || pending.kind == "reconcile")
                        AcceptRemote(local, incoming, raw.id);
                    else pending.remoteConflict = incoming;
                    continue;
                }
                bool unknownCreate = !state.remoteIds.ContainsKey(id) && pending.mayHaveReachedServer;
                if (pending.kind == "reconcile")
                {
                    if (Fingerprint(pending.payload) == remoteHash)
                    { state.remoteIds[id] = raw.id; state.acknowledgedHashes[id] = remoteHash; state.pending.Remove(pending); }
                    else if (pending.baseHash == remoteHash)
                    {
                        state.remoteIds[id] = raw.id; state.acknowledgedHashes[id] = remoteHash;
                        pending.kind = "update"; pending.status = "pending"; pending.notBeforeUtcTicks = 0;
                    }
                    else AcceptRemote(local, incoming, raw.id);
                    continue;
                }
                if (unknownCreate)
                {
                    if (remoteHash != pending.lastSubmittedHash && remoteHash != Fingerprint(pending.payload))
                    { Conflict(pending, incoming, "Unconfirmed create differs from the server record."); continue; }
                    state.remoteIds[id] = raw.id;
                    state.acknowledgedHashes[id] = remoteHash;
                    pending.baseHash = remoteHash;
                    if (pending.kind != "delete") pending.kind = "update";
                    pending.status = "pending"; pending.notBeforeUtcTicks = 0; pending.mayHaveReachedServer = false;
                }
                else if (pending.kind == "create" && !pending.mayHaveReachedServer)
                { Conflict(pending, incoming, "Server ID collides with an unsent local feature."); continue; }
                else if (pending.mayHaveReachedServer && remoteHash == pending.lastSubmittedHash)
                {
                    // An earlier update can have committed even if its reply was
                    // lost. Keep a newer edit pending against that confirmed content.
                    pending.baseHash = remoteHash;
                    state.acknowledgedHashes[id] = remoteHash;
                }
                if (pending.kind != "delete" && Fingerprint(pending.payload) == remoteHash)
                { state.remoteIds[id] = raw.id; state.acknowledgedHashes[id] = remoteHash; state.pending.Remove(pending); }
                else if (pending.baseHash != remoteHash)
                    AcceptRemote(local, incoming, raw.id);
                // A local edit is sent only while the server still has its
                // original baseline. A competing server change wins.
                continue;
            }
            AcceptRemote(local, incoming, raw.id);
        }
        foreach (GeoJsonPendingEdit pending in state.pending.ToArray())
        {
            if (seen.Contains(pending.featureId)) continue;
            if (state.remoteIds.ContainsKey(pending.featureId) || state.acknowledgedHashes.ContainsKey(pending.featureId) ||
                pending.kind == "reconcile")
            { RemoveServerFeature(local, pending.featureId); continue; }
            if (pending.status == "conflict") { pending.remoteConflict = null; pending.remoteConflictId = null; continue; }
            // New local annotations remain queued. An ambiguous create stays in
            // reconcile state until its server identity can be confirmed.
        }
        foreach (string id in state.acknowledgedHashes.Keys.Union(state.remoteIds.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (!seen.Contains(id)) RemoveServerFeature(local, id);
        }
        return before != JsonConvert.SerializeObject(local);
    }

    private static void AcceptRemote(Root local, Feature incoming, string remoteId)
    {
        string id = incoming.id;
        local.features.RemoveAll(f => f != null && string.Equals(f.id, id, StringComparison.OrdinalIgnoreCase));
        local.features.Add(incoming);
        local.syncState.remoteIds[id] = remoteId;
        local.syncState.acknowledgedHashes[id] = Fingerprint(incoming);
        local.syncState.pending.RemoveAll(p => string.Equals(p.featureId, id, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveServerFeature(Root local, string id)
    {
        local.features.RemoveAll(f => f != null && string.Equals(f.id, id, StringComparison.OrdinalIgnoreCase));
        local.syncState.pending.RemoveAll(p => string.Equals(p.featureId, id, StringComparison.OrdinalIgnoreCase));
        local.syncState.acknowledgedHashes.Remove(id);
        local.syncState.remoteIds.Remove(id);
    }

    private static void Conflict(GeoJsonPendingEdit edit, Feature remote, string reason)
    { edit.status = "conflict"; edit.remoteConflict = Clone(remote); edit.error = reason; }

    public static bool Resolve(Root root, string id, bool keepLocal)
    {
        GeoJsonPendingEdit edit = Find(root, id);
        if (edit == null || (edit.status != "conflict" && edit.status != "reconcile")) return false;
        if (!keepLocal)
        {
            root.features.RemoveAll(f => f != null && string.Equals(f.id, id, StringComparison.OrdinalIgnoreCase));
            if (edit.remoteConflict != null)
            {
                Feature accepted = Clone(edit.remoteConflict);
                root.features.Add(accepted);
                root.syncState.remoteIds[id] = edit.remoteConflictId ?? accepted.id;
                root.syncState.acknowledgedHashes[id] = Fingerprint(accepted);
            }
            else { root.syncState.remoteIds.Remove(id); root.syncState.acknowledgedHashes.Remove(id); }
            root.syncState.pending.Remove(edit);
            return true;
        }
        if (edit.remoteConflict != null)
        {
            root.syncState.remoteIds[id] = edit.remoteConflictId ?? edit.remoteConflict.id;
            edit.baseHash = Fingerprint(edit.remoteConflict);
            root.syncState.acknowledgedHashes[id] = edit.baseHash;
        }
        else
        { root.syncState.remoteIds.Remove(id); root.syncState.acknowledgedHashes.Remove(id); edit.baseHash = null; }
        bool live = root.features.Any(f => f != null && string.Equals(f.id, id, StringComparison.OrdinalIgnoreCase));
        edit.kind = live ? (root.syncState.remoteIds.ContainsKey(id) ? "update" : "create") : "delete";
        if (!live && !root.syncState.remoteIds.ContainsKey(id)) { root.syncState.pending.Remove(edit); return true; }
        edit.status = "pending"; edit.revision = root.syncState.nextRevision++; edit.operationId = Guid.NewGuid().ToString("N");
        edit.attempts = 0; edit.notBeforeUtcTicks = 0; edit.mayHaveReachedServer = false; edit.error = null;
        edit.remoteConflict = null;
        return true;
    }
}

