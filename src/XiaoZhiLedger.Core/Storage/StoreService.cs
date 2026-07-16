using System.Text.Json;
using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Storage;

public sealed class StoreService
{
    private readonly string _storePath;

    public StoreService(string dataDirectory)
    {
        _storePath = Path.Combine(dataDirectory, "store.json");
    }

    public async Task<StoreReadResult> LoadReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storePath))
        {
            return StoreReadResult.Success(
                CreateEmptySnapshot(),
                "没有发现既有账本。当前仍保持只读模式。");
        }

        try
        {
            var json = await File.ReadAllTextAsync(_storePath, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("账本顶层必须是 JSON 对象。");
            }

            var snapshot = ParseSnapshot(document.RootElement);
            return StoreReadResult.Success(
                snapshot,
                $"已只读加载 {snapshot.Projects.Count} 个项目和 {snapshot.RecordCount} 条历史记录。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return StoreReadResult.Failure(
                "账本无法安全读取。原文件保持不变，请继续使用稳定版并检查备份。",
                error);
        }
    }

    private LedgerStoreSnapshot ParseSnapshot(JsonElement root)
    {
        var settingsElement = GetObject(root, "settings");
        var legacyRootPath = ReadString(settingsElement, "rootPath");
        var settings = new LedgerSettings(
            legacyRootPath,
            ReadString(settingsElement, "ffmpegPath"),
            ReadString(settingsElement, "currentProjectId"),
            ReadDouble(settingsElement, "windowWidth", 1500),
            ReadDouble(settingsElement, "windowHeight", 900),
            ReadBoolean(settingsElement, "watchEnabled", true),
            NormalizeCardScale(ReadDouble(settingsElement, "cardScale", 1.0)));

        var projects = ParseProjects(root, legacyRootPath);
        var records = ParseRecords(root);

        return new LedgerStoreSnapshot(
            _storePath,
            SourceExists: true,
            settings,
            projects,
            records);
    }

    private static IReadOnlyList<LedgerProject> ParseProjects(JsonElement root, string legacyRootPath)
    {
        if (!TryGetProperty(root, "projects", out var projectsElement))
        {
            return Array.Empty<LedgerProject>();
        }

        var projectElements = projectsElement.ValueKind switch
        {
            JsonValueKind.Array => projectsElement.EnumerateArray().Where(
                item => item.ValueKind == JsonValueKind.Object),
            JsonValueKind.Object => new[] { projectsElement }.AsEnumerable(),
            _ => Enumerable.Empty<JsonElement>()
        };

        var projects = new List<LedgerProject>();
        foreach (var projectElement in projectElements)
        {
            var hasProjectRoot = TryGetProperty(projectElement, "rootPath", out var rootElement);
            var projectRoot = hasProjectRoot ? ReadString(rootElement) : legacyRootPath;
            var hasSelectedFolder = TryGetProperty(projectElement, "selectedFolder", out var selectedElement);
            var selectedFolder = hasSelectedFolder ? ReadString(selectedElement) : projectRoot;

            projects.Add(new LedgerProject(
                ReadString(projectElement, "id"),
                ReadString(projectElement, "name"),
                projectRoot,
                selectedFolder,
                ReadString(projectElement, "createdAt")));
        }

        return projects;
    }

    private LedgerStoreSnapshot CreateEmptySnapshot()
    {
        var settings = new LedgerSettings("", "", "", 1500, 900, true, 1.0);
        return new LedgerStoreSnapshot(
            _storePath,
            SourceExists: false,
            settings,
            Array.Empty<LedgerProject>(),
            new Dictionary<string, LedgerRecordSnapshot>(StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, LedgerRecordSnapshot> ParseRecords(JsonElement root)
    {
        var result = new Dictionary<string, LedgerRecordSnapshot>(StringComparer.OrdinalIgnoreCase);
        if (GetObject(root, "records") is not { } records)
        {
            return result;
        }

        foreach (var recordProperty in records.EnumerateObject())
        {
            if (recordProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var record = recordProperty.Value;
            var projectStates = new Dictionary<string, LedgerProjectMaterialState>(StringComparer.OrdinalIgnoreCase);
            if (GetObject(record, "projects") is { } projects)
            {
                foreach (var projectProperty in projects.EnumerateObject())
                {
                    if (projectProperty.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var state = projectProperty.Value;
                    projectStates[projectProperty.Name] = new LedgerProjectMaterialState(
                        NormalizeStatus(ReadString(state, "status")),
                        ReadInteger(state, "dragCount", 0),
                        ReadString(state, "updatedAt"),
                        ReadString(state, "lastDraggedAt"));
                }
            }

            result[recordProperty.Name] = new LedgerRecordSnapshot(
                recordProperty.Name,
                ReadString(record, "path"),
                ReadString(record, "name"),
                ReadLong(record, "size", 0),
                ReadString(record, "lastSeen"),
                ReadString(record, "note"),
                projectStates);
        }

        return result;
    }

    private static JsonElement? GetObject(JsonElement parent, string propertyName)
    {
        return TryGetProperty(parent, propertyName, out var value)
            && value.ValueKind == JsonValueKind.Object
                ? value
                : null;
    }

    private static bool TryGetProperty(JsonElement parent, string propertyName, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ReadString(JsonElement? parent, string propertyName)
    {
        return parent is { } element && TryGetProperty(element, propertyName, out var value)
            ? ReadString(value)
            : "";
    }

    private static string ReadString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => ""
        };
    }

    private static double ReadDouble(JsonElement? parent, string propertyName, double fallback)
    {
        if (parent is not { } element || !TryGetProperty(element, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), out number)
                ? number
                : fallback;
    }

    private static bool ReadBoolean(JsonElement? parent, string propertyName, bool fallback)
    {
        if (parent is not { } element || !TryGetProperty(element, propertyName, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var result) => result,
            _ => fallback
        };
    }

    private static int ReadInteger(JsonElement? parent, string propertyName, int fallback)
    {
        var number = ReadLong(parent, propertyName, fallback);
        return number is >= int.MinValue and <= int.MaxValue ? (int)number : fallback;
    }

    private static long ReadLong(JsonElement? parent, string propertyName, long fallback)
    {
        if (parent is not { } element || !TryGetProperty(element, propertyName, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), out number)
                ? number
                : fallback;
    }

    private static string NormalizeStatus(string status) => status switch
    {
        "used" => "used",
        "candidate" => "candidate",
        "excluded" => "excluded",
        _ => "unused"
    };

    private static double NormalizeCardScale(double scale)
    {
        var allowed = new[] { 0.7, 0.8, 1.0, 1.2, 1.4 };
        return allowed.Any(value => Math.Abs(value - scale) < 0.001) ? scale : 1.0;
    }
}
