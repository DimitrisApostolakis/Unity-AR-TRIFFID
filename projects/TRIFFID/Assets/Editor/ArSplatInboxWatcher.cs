using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using GaussianSplatting.Runtime;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Receives AR jobs from the TRIFFID pipeline. As everywhere else in the pipeline, files travel
/// through a shared folder and RabbitMQ only carries the notification: the ground station Unity
/// instance (ArJobPublisher) writes the converted Gaussian splat asset, the mesh obj and the
/// geolocal transform json to the samba folder and publishes "ar.ready". Each job is imported
/// into the project, its paths are written to project_paths.json and <see cref="ArSceneStager"/>
/// points the AR scene at them.
/// </summary>
[InitializeOnLoad]
public static class ArSplatInboxWatcher
{
    private const string LogPrefix = "[ArSplatInboxWatcher]";

    // Each job is imported into its own folder, Assets/TriffidJobs/<stem>/.
    private const string JobsFolderAssetPath = "Assets/TriffidJobs";
    private const double PollIntervalSeconds = 10.0;

    // Shared config consumed by the renderer, mesh loader, COLMAP aligner and JSON spawner.
    internal const string ProjectPathsJsonFileName = "project_paths.json";
    internal const string GaussianAssetPathJsonKey = "gaussian_splat_asset_path";
    internal const string MeshPathJsonKey = "mesh_path";
    internal const string ColmapTransformPathJsonKey = "colmap_aligner_transform_path";

    // Settings are resolved in this order so credentials are never hardcoded:
    //   1. a real environment variable, which always wins
    //   2. a .env file found by walking up from the Unity project (the AR repo root on the
    //      laptop), or pointed at explicitly with TRIFFID_ENV_FILE
    //   3. the fallback baked in below
    // An Editor started from Unity Hub inherits no shell environment, so on the laptop the .env
    // file is the normal way to set the broker address and credentials.
    private const int DotEnvSearchDepth = 6;
    private const string DotEnvFileName = ".env";
    private const string DotEnvPathOverrideVariable = "TRIFFID_ENV_FILE";

    // Set by LoadDotEnv; null when no .env was found. Declared first so the initializer below
    // can fill it in (static field initializers run in declaration order).
    private static string s_DotEnvPath;
    private static readonly Dictionary<string, string> s_DotEnv = LoadDotEnv();

    // The laptop is outside the Docker network: RABBITMQ_HOST is the pipeline machine's address.
    private static readonly string RabbitMqHost = GetEnv("RABBITMQ_HOST", "localhost");
    private static readonly int RabbitMqPort = GetEnvInt("RABBITMQ_PORT", 5672);
    private static readonly string RabbitMqUser = GetEnv("RABBITMQ_USER", "guest");
    private static readonly string RabbitMqPass = GetEnv("RABBITMQ_PASS", "guest");
    private static readonly string RabbitMqVirtualHost = GetEnv("RABBITMQ_VHOST", "/");
    private static readonly string RabbitMqExchange = GetEnv("RABBITMQ_EXCHANGE", "pipeline.events");
    private static readonly string RabbitMqArRoutingKey = GetEnv("RABBITMQ_AR_ROUTING_KEY", "ar.ready");
    private static readonly string RabbitMqQueueName = GetEnv("RABBITMQ_AR_QUEUE", "ar.queue");
    private const bool RabbitMqQueueDurable = true;

    // Shared folder the job files arrive in. TRIFFID_SAMBA_DIR (env or .env) when set, e.g. the
    // mounted share (\\server\share or a mapped drive); otherwise the samba_folder directory found
    // by walking up from the Unity project, which is the one at the AR repo root.
    private const string SambaDirVariable = "TRIFFID_SAMBA_DIR";
    private const string SambaFolderName = "samba_folder";
    private static readonly string s_SambaDir = ResolveSambaDir();

    private static string GetEnv(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && !s_DotEnv.TryGetValue(name, out value))
            value = null;

        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int GetEnvInt(string name, int fallback)
    {
        return int.TryParse(GetEnv(name, null), out var parsed) ? parsed : fallback;
    }

    // Minimal .env reader matching what docker compose accepts: KEY=VALUE lines, # comments,
    // optional `export` prefix, optionally quoted values. Values are otherwise taken literally, so
    // Windows paths with backslashes need no escaping.
    private static Dictionary<string, string> LoadDotEnv()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = FindDotEnvFile();
        if (string.IsNullOrEmpty(path))
            return values;

        try
        {
            foreach (var rawLine in File.ReadAllLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line.Substring("export ".Length).TrimStart();

                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line.Substring(0, separator).TrimEnd();
                var value = line.Substring(separator + 1).Trim();

                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                else
                {
                    // Unquoted value: an inline comment starts at the first " #", as in compose.
                    var comment = value.IndexOf(" #", StringComparison.Ordinal);
                    if (comment >= 0)
                        value = value.Substring(0, comment).TrimEnd();
                }

                values[key] = value;
            }

            s_DotEnvPath = path;
        }
        catch (Exception ex)
        {
            values.Clear();
            Debug.LogWarning($"{LogPrefix} Could not read '{path}': {ex.Message}");
        }

        return values;
    }

    private static string FindDotEnvFile()
    {
        var overridePath = Environment.GetEnvironmentVariable(DotEnvPathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
                return overridePath;

            Debug.LogWarning($"{LogPrefix} {DotEnvPathOverrideVariable} points at '{overridePath}', which does not exist.");
            return null;
        }

        return FindAboveProject(directory =>
        {
            var candidate = Path.Combine(directory, DotEnvFileName);
            return File.Exists(candidate) ? candidate : null;
        });
    }

    private static string ResolveSambaDir()
    {
        var configured = GetEnv(SambaDirVariable, null);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        return FindAboveProject(directory =>
        {
            var candidate = Path.Combine(directory, SambaFolderName);
            return Directory.Exists(candidate) ? candidate : null;
        });
    }

    // Walks up from the Unity project folder and returns the first non-null match.
    private static string FindAboveProject(Func<string, string> match)
    {
        string directory;
        try
        {
            directory = Directory.GetParent(Application.dataPath)?.FullName;
        }
        catch (Exception)
        {
            directory = null;
        }

        if (string.IsNullOrEmpty(directory))
            directory = Directory.GetCurrentDirectory();

        for (var depth = 0; depth < DotEnvSearchDepth && !string.IsNullOrEmpty(directory); depth++)
        {
            var found = match(directory);
            if (found != null)
                return found;

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    // Used in the connection-failure log so a silent fallback to the built-in guest/guest is
    // visible in the Console instead of showing up only as broker-side auth refusals.
    private static string DescribeCredentialSource()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RABBITMQ_USER")))
            return "environment variable RABBITMQ_USER";

        if (s_DotEnv.ContainsKey("RABBITMQ_USER"))
            return s_DotEnvPath;

        return s_DotEnvPath == null
            ? $"built-in default (no {DotEnvFileName} found above '{Application.dataPath}')"
            : $"built-in default (RABBITMQ_USER absent from '{s_DotEnvPath}')";
    }

    private static bool s_IsScanRunning;
    private static double s_NextPollAt;
    private static IConnection s_RabbitConnection;
    private static IChannel s_RabbitChannel;
    private static bool s_RabbitReady;

    static ArSplatInboxWatcher()
    {
        s_NextPollAt = EditorApplication.timeSinceStartup + 2.0;
        EditorApplication.update += OnEditorUpdate;
        AssemblyReloadEvents.beforeAssemblyReload += DisposeRabbitMq;

        Debug.Log($"{LogPrefix} Watching queue '{RabbitMqQueueName}' (exchange '{RabbitMqExchange}', routing key '{RabbitMqArRoutingKey}') on {RabbitMqHost}:{RabbitMqPort} as '{RabbitMqUser}' from {DescribeCredentialSource()}. " +
                  $"Samba folder: '{s_SambaDir ?? "(unresolved)"}'{(s_SambaDir != null && Directory.Exists(s_SambaDir) ? string.Empty : " (missing)")}.");
    }

    private static void OnEditorUpdate()
    {
        if (s_IsScanRunning)
            return;

        if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)
            return;

        // Jobs wait in the queue during Play Mode: importing assets and restaging the scene
        // would otherwise touch objects that are about to be reset when Play Mode exits.
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        var now = EditorApplication.timeSinceStartup;
        if (now < s_NextPollAt)
            return;

        s_NextPollAt = now + PollIntervalSeconds;
        ScanForJob();
    }

    private static void ScanForJob()
    {
        s_IsScanRunning = true;
        try
        {
            if (!TryInitializeRabbitMq())
                return;

            var delivery = s_RabbitChannel
                .BasicGetAsync(RabbitMqQueueName, autoAck: false, cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (delivery == null)
                return;

            ulong deliveryTag = delivery.DeliveryTag;

            try
            {
                string message = Encoding.UTF8.GetString(delivery.Body.ToArray()).Trim();
                if (string.IsNullOrWhiteSpace(message))
                {
                    Ack(deliveryTag);
                    return;
                }

                if (!TryParseMessage(message, out JObject job))
                {
                    Debug.LogWarning($"{LogPrefix} Queue message was not valid JSON, ignoring: '{message}'.");
                    Ack(deliveryTag);
                    return;
                }

                string splatMessagePath = ReadFirstString(job, "splat_asset_path");
                if (string.IsNullOrWhiteSpace(splatMessagePath))
                {
                    Debug.LogWarning($"{LogPrefix} Queue message has no 'splat_asset_path', ignoring: '{message}'.");
                    Ack(deliveryTag);
                    return;
                }

                if (!TryLocateJobFile(splatMessagePath, out string splatSourcePath))
                {
                    if (s_SambaDir == null || !Directory.Exists(s_SambaDir))
                    {
                        // An unreachable share is a connectivity or configuration problem, not a
                        // bad job: keep the message so it is picked up once the share is back.
                        Debug.LogWarning($"{LogPrefix} Samba folder '{s_SambaDir ?? "(unresolved)"}' is not reachable; requeueing job for '{splatMessagePath}'. Set {SambaDirVariable} to the shared folder.");
                        s_RabbitChannel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                        return;
                    }

                    Debug.LogWarning($"{LogPrefix} Queue message did not resolve to a splat asset in '{s_SambaDir}': '{splatMessagePath}'. Message: {message}");
                    Ack(deliveryTag);
                    return;
                }

                Debug.Log($"{LogPrefix} Received job '{job["job_id"]}' for '{splatSourcePath}': {message}");

                StagedJob staged;
                try
                {
                    staged = ImportJobFiles(job, splatSourcePath);
                }
                catch (JobFileException ex)
                {
                    // A file the job names is missing or unusable: resending the same message
                    // cannot fix that, so the job is dropped with the reason.
                    Debug.LogError($"{LogPrefix} Job '{job["job_id"]}' was not imported: {ex.Message}");
                    Ack(deliveryTag);
                    return;
                }

                var sceneInputs = ApplyJobAssets(job, staged);
                ArSceneStager.Stage(sceneInputs);

                Debug.Log($"{LogPrefix} Imported job '{job["job_id"]}' into '{staged.FolderAssetPath}'.");
                Ack(deliveryTag);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Queue message handling failed: {ex.Message}");
                s_RabbitChannel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        finally
        {
            s_IsScanRunning = false;
        }
    }

    private static void Ack(ulong deliveryTag)
    {
        s_RabbitChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
    }

    private static bool TryInitializeRabbitMq()
    {
        if (s_RabbitReady && s_RabbitConnection?.IsOpen == true && s_RabbitChannel?.IsOpen == true)
            return true;

        DisposeRabbitMq();

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = RabbitMqHost,
                Port = RabbitMqPort,
                UserName = RabbitMqUser,
                Password = RabbitMqPass,
                VirtualHost = RabbitMqVirtualHost,
            };

            s_RabbitConnection = factory.CreateConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            s_RabbitChannel = s_RabbitConnection.CreateChannelAsync(new CreateChannelOptions(false, false), CancellationToken.None).GetAwaiter().GetResult();

            // Same topic exchange as the rest of the pipeline; the AR jobs get their own routing
            // key and durable queue so they wait while the laptop is offline.
            s_RabbitChannel.ExchangeDeclareAsync(
                exchange: RabbitMqExchange,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            s_RabbitChannel.QueueDeclareAsync(
                queue: RabbitMqQueueName,
                durable: RabbitMqQueueDurable,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: false,
                noWait: false,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
            s_RabbitChannel.QueueBindAsync(
                queue: RabbitMqQueueName,
                exchange: RabbitMqExchange,
                routingKey: RabbitMqArRoutingKey,
                arguments: null,
                noWait: false,
                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            s_RabbitReady = true;
            Debug.Log($"{LogPrefix} Connected to RabbitMQ at {RabbitMqHost}:{RabbitMqPort}, polling '{RabbitMqQueueName}' every {PollIntervalSeconds:0}s.");
            return true;
        }
        catch (Exception ex)
        {
            s_RabbitReady = false;
            Debug.LogWarning($"{LogPrefix} RabbitMQ connection failed ({RabbitMqHost}:{RabbitMqPort}, vhost '{RabbitMqVirtualHost}', queue '{RabbitMqQueueName}', user '{RabbitMqUser}' from {DescribeCredentialSource()}): {ex.Message}");
            return false;
        }
    }

    private static void DisposeRabbitMq()
    {
        try
        {
            s_RabbitChannel?.Dispose();
            s_RabbitConnection?.Dispose();
        }
        catch
        {
        }
        finally
        {
            s_RabbitChannel = null;
            s_RabbitConnection = null;
            s_RabbitReady = false;
        }
    }

    // Resolves a path from the ar.ready message to a file in the samba folder. The sender's paths
    // mean nothing on the laptop, so a path is tried as a location inside the samba folder, then
    // by file name in the samba folder. An absolute path that exists here (e.g. a UNC path to the
    // share) and a manual Assets/... path are accepted as well.
    private static bool TryLocateJobFile(string messagePath, out string absolutePath)
    {
        absolutePath = null;
        if (string.IsNullOrWhiteSpace(messagePath))
            return false;

        string fileName = Path.GetFileName(messagePath.Replace('\\', '/'));
        var candidates = new List<string>();
        if (Path.IsPathRooted(messagePath))
            candidates.Add(messagePath);
        if (s_SambaDir != null)
        {
            if (!Path.IsPathRooted(messagePath))
                candidates.Add(Path.Combine(s_SambaDir, messagePath));
            candidates.Add(Path.Combine(s_SambaDir, fileName));
        }
        if (messagePath.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            candidates.Add(AssetPathToAbsolute(messagePath));

        foreach (var candidate in candidates)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    absolutePath = Path.GetFullPath(candidate);
                    return true;
                }
            }
            catch (Exception)
            {
                // Malformed candidate (e.g. a foreign path format); try the next one.
            }
        }

        return false;
    }

    // Imports one job into Assets/TriffidJobs/<stem>/. The splat asset references its data files
    // by GUID, so the asset and every data file are copied together with their .meta files and
    // keep the ground station's GUIDs. The shared copies are left in place.
    private static StagedJob ImportJobFiles(JObject job, string splatSourcePath)
    {
        string stem = Path.GetFileNameWithoutExtension(splatSourcePath);
        string folderAssetPath = $"{JobsFolderAssetPath}/{stem}";

        // A resent job replaces its previous import. Keeping both would put the same GUIDs in the
        // project twice; Unity would then reassign one set and break the asset's data references.
        if (AssetDatabase.IsValidFolder(folderAssetPath))
            AssetDatabase.DeleteAsset(folderAssetPath);

        EnsureFolder("Assets", "TriffidJobs");
        EnsureFolder(JobsFolderAssetPath, stem);
        string folder = AssetPathToAbsolute(folderAssetPath);

        var staged = new StagedJob
        {
            FolderAssetPath = folderAssetPath,
            GaussianAssetPath = $"{folderAssetPath}/{CopyWithMeta(splatSourcePath, folder)}",
        };

        if (!(job["splat_data_paths"] is JArray dataPaths) || dataPaths.Count == 0)
            throw new JobFileException("'splat_data_paths' is missing or empty; the splat asset has no data to render.");

        foreach (var token in dataPaths)
        {
            string dataMessagePath = token.Type == JTokenType.String ? token.Value<string>() : null;
            if (!TryLocateJobFile(dataMessagePath, out string dataSourcePath))
                throw new JobFileException($"splat data file '{dataMessagePath}' was not found in '{s_SambaDir}'.");

            CopyWithMeta(dataSourcePath, folder);
        }

        staged.MeshPath = ImportCompanion(ReadFirstString(job, "obj_path", "mesh_path"), folder, folderAssetPath, "mesh OBJ");
        staged.TransformPath = ImportCompanion(ReadFirstString(job, "transform_json_path", "colmap_transform_path", "geolocalization_path"), folder, folderAssetPath, "geolocal transform JSON");

        // The files were written straight to disk, so the AssetDatabase has to discover them.
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

        var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(staged.GaussianAssetPath);
        if (asset == null)
            throw new JobFileException($"'{staged.GaussianAssetPath}' did not import as a GaussianSplatAsset.");
        if (asset.posData == null || asset.otherData == null || asset.colorData == null || asset.shData == null)
            throw new JobFileException($"'{staged.GaussianAssetPath}' imported without all of its data files; check that the job's .bytes files and their .meta files match the asset.");

        return staged;
    }

    private static string ImportCompanion(string messagePath, string folder, string folderAssetPath, string label)
    {
        if (string.IsNullOrWhiteSpace(messagePath))
            return null;

        if (!TryLocateJobFile(messagePath, out string sourcePath))
        {
            Debug.LogWarning($"{LogPrefix} {label} referenced by the job was not found in '{s_SambaDir ?? "(unresolved)"}': '{messagePath}'.");
            return null;
        }

        string fileName = Path.GetFileName(sourcePath);
        File.Copy(sourcePath, Path.Combine(folder, fileName), overwrite: true);
        return $"{folderAssetPath}/{fileName}";
    }

    // Returns the copied file name.
    private static string CopyWithMeta(string sourcePath, string folder)
    {
        string meta = sourcePath + ".meta";
        if (!File.Exists(meta))
            throw new JobFileException($"'{sourcePath}' has no .meta file next to it; without it the splat asset cannot find its data.");

        string fileName = Path.GetFileName(sourcePath);
        File.Copy(sourcePath, Path.Combine(folder, fileName), overwrite: true);
        File.Copy(meta, Path.Combine(folder, fileName + ".meta"), overwrite: true);
        return fileName;
    }

    private static bool TryParseMessage(string message, out JObject job)
    {
        job = null;
        try
        {
            job = JObject.Parse(message);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ReadFirstString(JObject job, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = job[key];
            if (token != null && token.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }

        return null;
    }

    // Writes the job into project_paths.json and returns the same paths for scene staging. Paths
    // are stored project-relative (Assets/...). Inputs the job does not carry are removed, so a
    // component never pairs the new splat with a previous job's files.
    private static ArSceneStager.SceneInputs ApplyJobAssets(JObject job, StagedJob staged)
    {
        var inputs = new ArSceneStager.SceneInputs
        {
            Job = job,
            GaussianAssetPath = staged.GaussianAssetPath,
            MeshPath = staged.MeshPath,
            TransformPath = staged.TransformPath,
        };

        UpdateProjectPaths(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GaussianAssetPathJsonKey] = inputs.GaussianAssetPath,
            [MeshPathJsonKey] = inputs.MeshPath,
            [ColmapTransformPathJsonKey] = inputs.TransformPath,
        });
        return inputs;
    }

    // A null value removes the key.
    private static void UpdateProjectPaths(IDictionary<string, string> keyValues)
    {
        if (keyValues == null || keyValues.Count == 0)
            return;

        try
        {
            string streamingAssetsPath = Application.streamingAssetsPath;
            EnsureFolder("Assets", "StreamingAssets");
            Directory.CreateDirectory(streamingAssetsPath);

            string configPath = Path.Combine(streamingAssetsPath, ProjectPathsJsonFileName);

            // Merge into any existing config so unrelated keys are preserved.
            JObject root = null;
            if (File.Exists(configPath))
            {
                try
                {
                    string existing = File.ReadAllText(configPath);
                    if (!string.IsNullOrWhiteSpace(existing))
                        root = JObject.Parse(existing);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{LogPrefix} Existing '{ProjectPathsJsonFileName}' could not be parsed, it will be rewritten: {ex.Message}");
                }
            }

            root ??= new JObject();

            foreach (var pair in keyValues)
            {
                if (pair.Value == null)
                    root.Remove(pair.Key);
                else
                    root[pair.Key] = pair.Value;
            }

            File.WriteAllText(configPath, root.ToString());

            string configAssetPath = AbsoluteToAssetPath(configPath);
            if (!string.IsNullOrEmpty(configAssetPath))
                AssetDatabase.ImportAsset(configAssetPath, ImportAssetOptions.ForceSynchronousImport);

            Debug.Log($"{LogPrefix} Updated '{ProjectPathsJsonFileName}': {string.Join(", ", keyValues.Keys)}.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{LogPrefix} Could not update '{ProjectPathsJsonFileName}': {ex.Message}");
        }
    }

    private static void EnsureFolder(string parentAssetPath, string folderName)
    {
        string childPath = $"{parentAssetPath}/{folderName}";
        if (AssetDatabase.IsValidFolder(childPath))
            return;

        AssetDatabase.CreateFolder(parentAssetPath, folderName);
    }

    private static string AssetPathToAbsolute(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath))
            return string.Empty;

        if (!assetPath.StartsWith("Assets", StringComparison.Ordinal))
            return string.Empty;

        string relative = assetPath.Substring("Assets".Length).TrimStart('/', '\\');
        return Path.Combine(Application.dataPath, relative);
    }

    private static string AbsoluteToAssetPath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return string.Empty;

        string normalizedAbsolute = Path.GetFullPath(absolutePath).Replace('\\', '/');
        string normalizedAssetsRoot = Path.GetFullPath(Application.dataPath).Replace('\\', '/');

        if (!normalizedAbsolute.StartsWith(normalizedAssetsRoot + "/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedAbsolute, normalizedAssetsRoot, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        string relativeToAssets = normalizedAbsolute.Substring(normalizedAssetsRoot.Length).TrimStart('/');
        return string.IsNullOrEmpty(relativeToAssets) ? "Assets" : $"Assets/{relativeToAssets}";
    }

    private sealed class StagedJob
    {
        public string FolderAssetPath;
        public string GaussianAssetPath;
        public string MeshPath;
        public string TransformPath;
    }

    private sealed class JobFileException : Exception
    {
        public JobFileException(string message) : base(message)
        {
        }
    }
}
