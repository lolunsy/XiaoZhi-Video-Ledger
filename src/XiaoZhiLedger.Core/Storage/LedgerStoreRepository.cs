using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Storage;

public sealed class LedgerStoreRepository
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject? _root;

    public LedgerStoreRepository(string dataDirectory)
    {
        _storePath = Path.Combine(dataDirectory, "store.json");
    }

    public bool IsInitialized => _root is not null;

    public async Task<bool> CreateInitialStoreAsync(
        string projectName = "默认项目",
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(_storePath))
            {
                return false;
            }

            var project = new LedgerProject(
                Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(projectName) ? "默认项目" : projectName.Trim(),
                "",
                "",
                DateTimeOffset.Now.ToString("o"));
            var root = new JsonObject
            {
                ["settings"] = new JsonObject
                {
                    ["rootPath"] = "",
                    ["ffmpegPath"] = "",
                    ["currentProjectId"] = project.Id,
                    ["windowWidth"] = 1500,
                    ["windowHeight"] = 900,
                    ["watchEnabled"] = true,
                    ["cardScale"] = 1.0
                },
                ["projects"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = project.Id,
                        ["name"] = project.Name,
                        ["rootPath"] = "",
                        ["selectedFolder"] = "",
                        ["createdAt"] = project.CreatedAt
                    }
                },
                ["records"] = new JsonObject()
            };

            await SaveAtomicAsync(root, cancellationToken).ConfigureAwait(false);
            _root = root;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storePath))
        {
            throw new InvalidOperationException("没有可写入的既有 store.json。");
        }

        var json = await File.ReadAllTextAsync(_storePath, cancellationToken).ConfigureAwait(false);
        _root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        }) as JsonObject ?? throw new JsonException("账本顶层必须是 JSON 对象。");
    }

    public Task SetCurrentProjectAsync(string projectId, CancellationToken cancellationToken = default) =>
        MutateAsync(root => GetOrCreateObject(root, "settings")["currentProjectId"] = projectId, cancellationToken);

    public Task UpdateProjectWorkspaceAsync(
        string projectId,
        string rootPath,
        string selectedFolder,
        CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var project = FindProject(root, projectId)
                ?? throw new InvalidOperationException("当前项目已经不存在。");
            project["rootPath"] = rootPath;
            project["selectedFolder"] = selectedFolder;
            var settings = GetOrCreateObject(root, "settings");
            settings["rootPath"] = rootPath;
            settings["currentProjectId"] = projectId;
        }, cancellationToken);

    public Task SetCardScaleAsync(double scale, CancellationToken cancellationToken = default) =>
        MutateAsync(root => GetOrCreateObject(root, "settings")["cardScale"] = scale, cancellationToken);

    public Task SetWatchEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        MutateAsync(root => GetOrCreateObject(root, "settings")["watchEnabled"] = enabled, cancellationToken);

    public Task SetFfmpegPathAsync(string path, CancellationToken cancellationToken = default) =>
        MutateAsync(root => GetOrCreateObject(root, "settings")["ffmpegPath"] = path, cancellationToken);

    public Task EnsureRecordsAsync(
        IEnumerable<MediaItem> items,
        CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var records = GetOrCreateObject(root, "records");
            var now = DateTimeOffset.Now.ToString("o");
            foreach (var item in items)
            {
                var record = records[item.Id] as JsonObject;
                if (record is null)
                {
                    record = new JsonObject
                    {
                        ["note"] = "",
                        ["projects"] = new JsonObject()
                    };
                    records[item.Id] = record;
                }

                record["path"] = item.Path;
                record["name"] = item.Name;
                record["size"] = item.Size;
                record["lastSeen"] = now;
                if (!record.ContainsKey("note"))
                {
                    record["note"] = "";
                }

                if (record["projects"] is not JsonObject)
                {
                    record["projects"] = new JsonObject();
                }
            }
        }, cancellationToken);

    public Task SetNoteAsync(string itemId, string note, CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var record = GetOrCreateRecord(root, itemId);
            record["note"] = note;
        }, cancellationToken);

    public Task SetProjectStateAsync(
        string itemId,
        string projectId,
        string status,
        bool incrementDragCount,
        CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var record = GetOrCreateRecord(root, itemId);
            var projects = GetOrCreateObject(record, "projects");
            var state = projects[projectId] as JsonObject ?? new JsonObject();
            projects[projectId] = state;
            state["status"] = NormalizeStatus(status);
            state["dragCount"] = Math.Max(0, ReadInt(state["dragCount"]));
            state["updatedAt"] = DateTimeOffset.Now.ToString("o");
            if (incrementDragCount)
            {
                state["dragCount"] = ReadInt(state["dragCount"]) + 1;
                state["lastDraggedAt"] = DateTimeOffset.Now.ToString("o");
            }
        }, cancellationToken);

    public Task RestoreProjectStateAsync(
        string itemId,
        string projectId,
        LedgerProjectMaterialState? state,
        CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var record = GetOrCreateRecord(root, itemId);
            var projects = GetOrCreateObject(record, "projects");
            if (state is null)
            {
                projects.Remove(projectId);
                return;
            }

            projects[projectId] = new JsonObject
            {
                ["status"] = NormalizeStatus(state.Status),
                ["dragCount"] = Math.Max(0, state.DragCount),
                ["updatedAt"] = state.UpdatedAt,
                ["lastDraggedAt"] = state.LastDraggedAt
            };
        }, cancellationToken);

    public async Task<LedgerProject> CreateProjectAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var project = new LedgerProject(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            "",
            "",
            DateTimeOffset.Now.ToString("o"));
        await MutateAsync(root =>
        {
            var projects = GetOrCreateArray(root, "projects");
            projects.Add(new JsonObject
            {
                ["id"] = project.Id,
                ["name"] = project.Name,
                ["rootPath"] = "",
                ["selectedFolder"] = "",
                ["createdAt"] = project.CreatedAt
            });
            GetOrCreateObject(root, "settings")["currentProjectId"] = project.Id;
        }, cancellationToken).ConfigureAwait(false);
        return project;
    }

    public Task RenameProjectAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var project = FindProject(root, projectId)
                ?? throw new InvalidOperationException("项目不存在。");
            project["name"] = name.Trim();
        }, cancellationToken);

    public Task DeleteProjectAsync(
        string projectId,
        string nextProjectId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            var projects = GetOrCreateArray(root, "projects");
            if (projects.Count <= 1)
            {
                throw new InvalidOperationException("至少需要保留一个项目。");
            }

            var index = -1;
            for (var position = 0; position < projects.Count; position++)
            {
                if (string.Equals(
                        projects[position]?["id"]?.GetValue<string>(),
                        projectId,
                        StringComparison.Ordinal))
                {
                    index = position;
                    break;
                }
            }

            if (index >= 0)
            {
                projects.RemoveAt(index);
            }
            else
            {
                throw new InvalidOperationException("要删除的项目不存在。");
            }

            foreach (var recordProperty in GetOrCreateObject(root, "records").ToList())
            {
                if (recordProperty.Value is JsonObject record
                    && record["projects"] is JsonObject states)
                {
                    states.Remove(projectId);
                }
            }

            GetOrCreateObject(root, "settings")["currentProjectId"] = nextProjectId;
        }, cancellationToken);

    public Task ResetStatesAsync(string? projectId, CancellationToken cancellationToken = default) =>
        MutateAsync(root =>
        {
            foreach (var recordProperty in GetOrCreateObject(root, "records").ToList())
            {
                if (recordProperty.Value is not JsonObject record)
                {
                    continue;
                }

                if (projectId is null)
                {
                    record["projects"] = new JsonObject();
                }
                else if (record["projects"] is JsonObject states)
                {
                    states.Remove(projectId);
                }
            }
        }, cancellationToken);

    private async Task MutateAsync(Action<JsonObject> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _root ?? throw new InvalidOperationException("账本仓库尚未初始化。");
            var working = (JsonObject)current.DeepClone();
            mutation(working);
            await SaveAtomicAsync(working, cancellationToken).ConfigureAwait(false);
            _root = working;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAtomicAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_storePath)
            ?? throw new InvalidOperationException("账本路径无效。");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"store.{Guid.NewGuid():N}.tmp");
        var lastGoodPath = Path.Combine(directory, "store.last-good.json");
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65_536,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_storePath))
            {
                File.Replace(tempPath, _storePath, lastGoodPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _storePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject value)
        {
            return value;
        }

        value = new JsonObject();
        parent[propertyName] = value;
        return value;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonArray value)
        {
            return value;
        }

        value = new JsonArray();
        parent[propertyName] = value;
        return value;
    }

    private static JsonObject GetOrCreateRecord(JsonObject root, string itemId)
    {
        var records = GetOrCreateObject(root, "records");
        if (records[itemId] is JsonObject record)
        {
            return record;
        }

        record = new JsonObject
        {
            ["path"] = "",
            ["name"] = "",
            ["size"] = 0,
            ["lastSeen"] = DateTimeOffset.Now.ToString("o"),
            ["note"] = "",
            ["projects"] = new JsonObject()
        };
        records[itemId] = record;
        return record;
    }

    private static JsonObject? FindProject(JsonObject root, string projectId)
    {
        return GetOrCreateArray(root, "projects")
            .OfType<JsonObject>()
            .FirstOrDefault(project => string.Equals(
                project["id"]?.GetValue<string>(), projectId, StringComparison.Ordinal));
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var integer))
            {
                return Math.Max(0, integer);
            }

            if (value.TryGetValue<string>(out var text) && int.TryParse(text, out integer))
            {
                return Math.Max(0, integer);
            }
        }

        return 0;
    }

    private static string NormalizeStatus(string status) => status switch
    {
        "used" => "used",
        "candidate" => "candidate",
        "excluded" => "excluded",
        _ => "unused"
    };
}
