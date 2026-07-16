using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using XiaoZhiLedger.Core.Models;
using XiaoZhiLedger.Core.Services;
using XiaoZhiLedger.Core.Storage;

var tests = new (string Name, Func<Task> Run)[]
{
    ("parses stable store without changing source", ParseStableStoreWithoutChangingSource),
    ("inherits legacy project paths in memory", InheritLegacyProjectPaths),
    ("selects current project and normalizes settings", SelectCurrentProjectAndNormalizeSettings),
    ("accepts the 70 percent card scale", AcceptSeventyPercentCardScale),
    ("supports single project object", SupportSingleProjectObject),
    ("leaves invalid json untouched", LeaveInvalidJsonUntouched),
    ("handles a missing store without creating files", HandleMissingStoreWithoutCreatingFiles),
    ("creates a safe first-run store exactly once", CreateSafeFirstRunStoreExactlyOnce),
    ("creates one byte-identical migration backup", CreateOneByteIdenticalMigrationBackup),
    ("keeps at most ten migration backups", KeepAtMostTenMigrationBackups),
    ("matches the stable fingerprint algorithm", MatchStableFingerprintAlgorithm),
    ("keeps fingerprints when content moves", KeepFingerprintWhenContentMoves),
    ("scans supported media and real folders", ScanSupportedMediaAndRealFolders),
    ("normalizes pasted paths", NormalizePastedPaths),
    ("atomically preserves unknown store fields", AtomicallyPreserveUnknownStoreFields),
    ("writes and restores project material state", WriteAndRestoreProjectMaterialState),
    ("manages projects without touching records from others", ManageProjectsSafely),
    ("debounces folder watch changes", DebounceFolderWatchChanges),
    ("generates ffmpeg visual caches when configured", GenerateFfmpegVisualCachesWhenConfigured)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception error)
    {
        failures.Add($"{test.Name}: {error.Message}");
        Console.WriteLine($"FAIL  {test.Name}");
        Console.WriteLine($"      {error.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} checks passed.");
return failures.Count == 0 ? 0 : 1;

static async Task ParseStableStoreWithoutChangingSource()
{
    await WithTempDirectory(async dataDirectory =>
    {
        var storePath = await WriteStore(dataDirectory, StableStoreJson());
        var beforeHash = HashFile(storePath);
        var result = await new StoreService(dataDirectory).LoadReadOnlyAsync();
        var afterHash = HashFile(storePath);

        Assert(result.IsSuccess, result.UserMessage);
        Assert(result.Snapshot is not null, "Snapshot was null.");
        Assert(result.Snapshot!.Projects.Count == 2, "Expected two projects.");
        Assert(result.Snapshot.RecordCount == 2, "Expected two records.");
        Assert(beforeHash == afterHash, "Read-only load changed store.json.");
    });
}

static async Task InheritLegacyProjectPaths()
{
    await WithTempDirectory(async dataDirectory =>
    {
        await WriteStore(dataDirectory, StableStoreJson());
        var result = await new StoreService(dataDirectory).LoadReadOnlyAsync();
        var projects = result.Snapshot!.Projects;

        Assert(projects[0].RootPath == @"D:\Legacy素材", "Missing project root did not inherit settings.rootPath.");
        Assert(projects[0].SelectedFolder == @"D:\Legacy素材", "Missing selected folder did not inherit project root.");
        Assert(projects[1].RootPath == "", "An explicit empty project root must stay empty.");
        Assert(projects[1].SelectedFolder == "", "An explicit empty selected folder must stay empty.");
    });
}

static async Task SelectCurrentProjectAndNormalizeSettings()
{
    await WithTempDirectory(async dataDirectory =>
    {
        await WriteStore(dataDirectory, StableStoreJson());
        var snapshot = (await new StoreService(dataDirectory).LoadReadOnlyAsync()).Snapshot!;

        Assert(snapshot.CurrentProject?.Id == "project-b", "currentProjectId was not respected.");
        Assert(Math.Abs(snapshot.Settings.CardScale - 1.0) < 0.001, "Invalid card scale was not normalized in memory.");
        Assert(snapshot.Settings.WatchEnabled, "String boolean setting was not parsed.");
    });
}

static async Task AcceptSeventyPercentCardScale()
{
    await WithTempDirectory(async dataDirectory =>
    {
        const string json = """
        {
          "settings": { "cardScale": 0.7 },
          "projects": [],
          "records": {}
        }
        """;
        await WriteStore(dataDirectory, json);
        var snapshot = (await new StoreService(dataDirectory).LoadReadOnlyAsync()).Snapshot!;

        Assert(Math.Abs(snapshot.Settings.CardScale - 0.7) < 0.001,
            "The 70 percent card scale was not preserved.");
    });
}

static async Task SupportSingleProjectObject()
{
    await WithTempDirectory(async dataDirectory =>
    {
        const string json = """
        {
          "settings": { "currentProjectId": "only" },
          "projects": { "id": "only", "name": "单项目", "rootPath": "", "selectedFolder": "" },
          "records": {}
        }
        """;
        await WriteStore(dataDirectory, json);
        var snapshot = (await new StoreService(dataDirectory).LoadReadOnlyAsync()).Snapshot!;
        Assert(snapshot.Projects.Count == 1, "Single object project was not accepted.");
        Assert(snapshot.CurrentProject?.DisplayName == "单项目", "Single object project was parsed incorrectly.");
    });
}

static async Task LeaveInvalidJsonUntouched()
{
    await WithTempDirectory(async dataDirectory =>
    {
        var storePath = await WriteStore(dataDirectory, "{ definitely not valid json");
        var beforeHash = HashFile(storePath);
        var result = await new StoreService(dataDirectory).LoadReadOnlyAsync();

        Assert(!result.IsSuccess, "Invalid JSON unexpectedly succeeded.");
        Assert(beforeHash == HashFile(storePath), "Invalid store was changed.");
        Assert(Directory.GetFiles(dataDirectory).Length == 1, "Read-only loader created unexpected files.");
    });
}

static async Task HandleMissingStoreWithoutCreatingFiles()
{
    await WithTempDirectory(async dataDirectory =>
    {
        var result = await new StoreService(dataDirectory).LoadReadOnlyAsync();
        Assert(result.IsSuccess, result.UserMessage);
        Assert(result.Snapshot is { SourceExists: false }, "Missing store state was not reported.");
        Assert(!File.Exists(Path.Combine(dataDirectory, "store.json")), "Loader created a new store.json.");
    });
}

static async Task CreateSafeFirstRunStoreExactlyOnce()
{
    await WithTempDirectory(async dataDirectory =>
    {
        var repository = new LedgerStoreRepository(dataDirectory);
        var created = await repository.CreateInitialStoreAsync("默认项目");
        var storePath = Path.Combine(dataDirectory, "store.json");
        var firstHash = HashFile(storePath);
        var result = await new StoreService(dataDirectory).LoadReadOnlyAsync();

        Assert(created, "The first-run store was not created.");
        Assert(result.IsSuccess && result.Snapshot is { SourceExists: true }, "The first-run store could not be loaded.");
        Assert(result.Snapshot!.Projects.Count == 1, "The first-run store did not contain one project.");
        Assert(result.Snapshot.CurrentProject?.Name == "默认项目", "The default project was not selected.");

        var createdAgain = await repository.CreateInitialStoreAsync("不应覆盖");
        Assert(!createdAgain, "An existing store was overwritten by first-run initialization.");
        Assert(HashFile(storePath) == firstHash, "The existing store changed during repeated initialization.");

        var secondProject = await repository.CreateProjectAsync("第二个项目");
        Assert(!string.IsNullOrWhiteSpace(secondProject.Id), "The new repository was not ready for writes.");
    });
}

static async Task CreateOneByteIdenticalMigrationBackup()
{
    await WithTempDirectory(async dataDirectory =>
    {
        var storePath = await WriteStore(dataDirectory, StableStoreJson());
        var service = new MigrationBackupService(dataDirectory);
        var first = service.EnsureBackup();
        var second = service.EnsureBackup();

        Assert(first.IsSuccess && first.WasCreated, "First migration backup was not created.");
        Assert(second.IsSuccess && !second.WasCreated, "Second call created a duplicate migration backup.");
        Assert(first.BackupPath is not null && File.Exists(first.BackupPath), "Backup file does not exist.");
        Assert(HashFile(storePath) == HashFile(first.BackupPath!), "Backup bytes differ from store.json.");
        Assert(Directory.GetFiles(Path.Combine(dataDirectory, "backups"), "store-before-csharp-migration-*.json").Length == 1,
            "Expected exactly one migration backup.");
    });
}

static async Task KeepAtMostTenMigrationBackups()
{
    await WithTempDirectory(async dataDirectory =>
    {
        await WriteStore(dataDirectory, StableStoreJson());
        var backupDirectory = Directory.CreateDirectory(Path.Combine(dataDirectory, "backups")).FullName;
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(backupDirectory, $"store-before-csharp-migration-20260101-0000{index:D2}-000.json");
            await File.WriteAllTextAsync(path, index.ToString());
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddMinutes(-index));
        }

        var result = new MigrationBackupService(dataDirectory).EnsureBackup();
        Assert(result.IsSuccess, result.UserMessage);
        Assert(Directory.GetFiles(backupDirectory, "store-before-csharp-migration-*.json").Length == 10,
            "Backup retention did not trim to ten files.");
    });
}

static async Task MatchStableFingerprintAlgorithm()
{
    await WithTempDirectory(async directory =>
    {
        var service = new MediaFingerprintService();
        foreach (var length in new[] { 0, 100, 65_536, 65_537, 150_000 })
        {
            var bytes = Enumerable.Range(0, length).Select(index => (byte)(index * 31 % 251)).ToArray();
            var path = Path.Combine(directory, $"sample-{length}.mp4");
            await File.WriteAllBytesAsync(path, bytes);
            var actual = await service.ComputeAsync(path);
            var expected = ReferenceFingerprint(bytes);
            Assert(actual == expected, $"Fingerprint mismatch for {length} bytes.");
        }
    });
}

static async Task KeepFingerprintWhenContentMoves()
{
    await WithTempDirectory(async directory =>
    {
        var bytes = Enumerable.Range(0, 90_000).Select(index => (byte)(index % 239)).ToArray();
        var first = Path.Combine(directory, "first.mp4");
        var nested = Directory.CreateDirectory(Path.Combine(directory, "nested")).FullName;
        var second = Path.Combine(nested, "second.mov");
        await File.WriteAllBytesAsync(first, bytes);
        await File.WriteAllBytesAsync(second, bytes);
        var service = new MediaFingerprintService();
        Assert(await service.ComputeAsync(first) == await service.ComputeAsync(second),
            "Equal content at different paths produced different fingerprints.");
    });
}

static async Task ScanSupportedMediaAndRealFolders()
{
    await WithTempDirectory(async directory =>
    {
        var clips = Directory.CreateDirectory(Path.Combine(directory, "Clips")).FullName;
        var empty = Directory.CreateDirectory(Path.Combine(directory, "EmptyFolder")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(directory, "root.mp4"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(clips, "nested.MOV"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(clips, "ignore.txt"), [7, 8, 9]);

        var scanner = new MediaScanner(new MediaFingerprintService());
        var result = await scanner.ScanAsync(directory);
        Assert(result.Items.Count == 2, "Scanner did not filter extensions correctly.");
        Assert(result.Directories.Contains(empty, StringComparer.OrdinalIgnoreCase), "Empty real folder was omitted.");
        Assert(result.Items.Any(item => item.RelativePath == Path.Combine("Clips", "nested.MOV")),
            "Nested relative path was incorrect.");

        var tree = FolderTreeBuilder.Build(directory, result.Directories, result.Items, _ => "unused");
        Assert(tree.TotalCount == 2 && tree.UnusedCount == 2, "Root folder counts were incorrect.");
        Assert(tree.Children.Any(node => node.FullPath == empty), "Empty folder was omitted from tree.");
    });
}

static Task NormalizePastedPaths()
{
    var expected = Path.Combine(Path.GetTempPath(), "素材 目录");
    var input = $"\"{expected}\"";
    Assert(MediaScanner.NormalizeInputPath(input) == expected, "Quoted path was not normalized.");
    return Task.CompletedTask;
}

static async Task AtomicallyPreserveUnknownStoreFields()
{
    await WithTempDirectory(async directory =>
    {
        await WriteStore(directory, StableStoreJson());
        var repository = new LedgerStoreRepository(directory);
        await repository.InitializeAsync();
        await repository.SetCardScaleAsync(1.4);
        await repository.SetWatchEnabledAsync(false);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "store.json")))!.AsObject();
        Assert(root["futureField"]?["mustRemainUntouched"]?.GetValue<bool>() == true,
            "Unknown future field was lost.");
        Assert(root["settings"]?["cardScale"]?.GetValue<double>() == 1.4, "Card scale was not saved.");
        Assert(File.Exists(Path.Combine(directory, "store.last-good.json")), "Atomic replace did not retain last-good backup.");
        Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "Temporary save file was left behind.");
    });
}

static async Task WriteAndRestoreProjectMaterialState()
{
    await WithTempDirectory(async directory =>
    {
        await WriteStore(directory, StableStoreJson());
        var repository = new LedgerStoreRepository(directory);
        await repository.InitializeAsync();
        await repository.SetProjectStateAsync("fingerprint-a", "project-b", "used", incrementDragCount: true);
        await repository.SetNoteAsync("fingerprint-a", "测试备注");
        var state = (await new StoreService(directory).LoadReadOnlyAsync()).Snapshot!.Records["fingerprint-a"];
        Assert(state.Note == "测试备注", "Note was not saved.");
        Assert(state.GetProjectState("project-b") is { Status: "used", DragCount: 1 }, "Drag state was not saved.");

        await repository.RestoreProjectStateAsync("fingerprint-a", "project-b", null);
        state = (await new StoreService(directory).LoadReadOnlyAsync()).Snapshot!.Records["fingerprint-a"];
        Assert(!state.Projects.ContainsKey("project-b"), "Undo did not remove a newly created state.");
    });
}

static async Task ManageProjectsSafely()
{
    await WithTempDirectory(async directory =>
    {
        await WriteStore(directory, StableStoreJson());
        var repository = new LedgerStoreRepository(directory);
        await repository.InitializeAsync();
        var project = await repository.CreateProjectAsync("第三项目");
        await repository.UpdateProjectWorkspaceAsync(project.Id, @"D:\NewRoot", @"D:\NewRoot\A");
        await repository.RenameProjectAsync(project.Id, "已重命名");
        var snapshot = (await new StoreService(directory).LoadReadOnlyAsync()).Snapshot!;
        var created = snapshot.Projects.Single(item => item.Id == project.Id);
        Assert(created.Name == "已重命名" && created.RootPath == @"D:\NewRoot", "Project mutations were incorrect.");

        await repository.DeleteProjectAsync(project.Id, "project-b");
        snapshot = (await new StoreService(directory).LoadReadOnlyAsync()).Snapshot!;
        Assert(snapshot.Projects.All(item => item.Id != project.Id), "Project was not deleted.");
        Assert(snapshot.Records.Count == 2, "Deleting a project deleted global material records.");
    });
}

static async Task DebounceFolderWatchChanges()
{
    await WithTempDirectory(async directory =>
    {
        using var watcher = new FolderWatchService();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventCount = 0;
        watcher.RefreshRequested += (_, _) =>
        {
            Interlocked.Increment(ref eventCount);
            completion.TrySetResult();
        };
        watcher.Start(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "新素材.mp4"), [1, 2, 3]);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await Task.Delay(400);
        Assert(eventCount == 1, $"Expected one debounced refresh, got {eventCount}.");
    });
}

static async Task GenerateFfmpegVisualCachesWhenConfigured()
{
    var configured = Environment.GetEnvironmentVariable("XIAOZHI_TEST_FFMPEG");
    if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured))
    {
        return;
    }

    await WithTempDirectory(async directory =>
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(directory, "媒体 空格")).FullName;
        var source = Path.Combine(sourceDirectory, "测试 样片.mp4");
        await RunProcess(configured,
        [
            "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "testsrc2=size=360x640:rate=25", "-f", "lavfi",
            "-i", "sine=frequency=1000:sample_rate=48000", "-t", "3", "-shortest",
            "-pix_fmt", "yuv420p", "-c:a", "aac", source
        ]);

        var file = new FileInfo(source);
        var fingerprint = await new MediaFingerprintService().ComputeAsync(source);
        var item = new MediaItem(fingerprint, source, file.Name, file.Extension, file.DirectoryName!, "", file.Name,
            file.Length, file.LastWriteTime, file.LastWriteTimeUtc);
        var service = new FfmpegService();
        var tools = service.ResolveToolset(directory, configured);
        Assert(tools is not null, "Configured FFmpeg was not resolved.");
        var resolvedTools = tools ?? throw new InvalidOperationException("Configured FFmpeg was not resolved.");
        var paths = new MediaCachePaths(directory);
        await service.GenerateThumbnailPairAsync(resolvedTools, item, paths.GetThumbnail(item));
        await service.GenerateScrubSpriteAsync(resolvedTools, item, paths.GetScrubSprite(item));
        await service.GenerateProxyAsync(resolvedTools, item, paths.GetProxy(item));
        Assert(new FileInfo(paths.GetThumbnail(item)).Length > 0, "Thumbnail cache is empty.");
        Assert(new FileInfo(paths.GetScrubSprite(item)).Length > 0, "Scrub cache is empty.");
        Assert(new FileInfo(paths.GetProxy(item)).Length > 0, "Proxy cache is empty.");
        if (!string.IsNullOrWhiteSpace(resolvedTools.FfprobePath))
        {
            var audioStream = await RunProcessCapture(resolvedTools.FfprobePath,
            [
                "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type",
                "-of", "default=noprint_wrappers=1:nokey=1", paths.GetProxy(item)
            ]);
            Assert(audioStream.Trim() == "audio", "Proxy cache did not preserve its audio stream.");
        }
    });
}

static async Task RunProcess(string executable, IReadOnlyList<string> arguments)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = System.Diagnostics.Process.Start(startInfo)
                        ?? throw new InvalidOperationException("Could not start FFmpeg fixture generation.");
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Assert(process.ExitCode == 0, $"FFmpeg fixture generation failed: {error}");
}

static async Task<string> RunProcessCapture(string executable, IReadOnlyList<string> arguments)
{
    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = System.Diagnostics.Process.Start(startInfo)
                        ?? throw new InvalidOperationException("Could not start process.");
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Assert(process.ExitCode == 0, $"Process failed: {error}");
    return output;
}

static string ReferenceFingerprint(byte[] bytes)
{
    using var stream = new MemoryStream();
    var metadata = Encoding.UTF8.GetBytes($"{bytes.LongLength}|");
    stream.Write(metadata);
    stream.Write(bytes, 0, Math.Min(65_536, bytes.Length));
    if (bytes.Length > 65_536)
    {
        stream.Write(bytes, bytes.Length - 65_536, 65_536);
    }

    return Convert.ToHexString(SHA1.HashData(stream.ToArray())).ToLowerInvariant();
}

static async Task<string> WriteStore(string dataDirectory, string json)
{
    var path = Path.Combine(dataDirectory, "store.json");
    await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return path;
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static async Task WithTempDirectory(Func<string, Task> action)
{
    var path = Path.Combine(Path.GetTempPath(), "XiaoZhiLedgerTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    try
    {
        await action(path);
    }
    finally
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static string StableStoreJson() => """
{
  "projects": [
    {
      "id": "project-a",
      "name": "旧项目",
      "createdAt": "2026-01-01T00:00:00.0000000+08:00"
    },
    {
      "id": "project-b",
      "name": "新项目",
      "rootPath": "",
      "selectedFolder": "",
      "createdAt": "2026-02-01T00:00:00.0000000+08:00"
    }
  ],
  "Settings": {
    "windowHeight": 900,
    "cardScale": "9.9",
    "watchEnabled": "true",
    "rootPath": "D:\\Legacy素材",
    "windowWidth": 1500,
    "ffmpegPath": "",
    "currentProjectId": "project-b"
  },
  "records": {
    "fingerprint-a": { "path": "A.mp4", "projects": {} },
    "fingerprint-b": { "path": "B.mov", "projects": {} }
  },
  "futureField": { "mustRemainUntouched": true }
}
""";
