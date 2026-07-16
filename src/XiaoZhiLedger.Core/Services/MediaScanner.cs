using System.Diagnostics;
using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Services;

public sealed class MediaScanner
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(
        [
            ".mp4", ".mov", ".mkv", ".avi", ".m4v", ".webm", ".flv",
            ".mts", ".m2ts", ".3gp", ".wmv", ".mpg", ".mpeg", ".ts"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly MediaFingerprintService _fingerprintService;

    public MediaScanner(MediaFingerprintService fingerprintService)
    {
        _fingerprintService = fingerprintService;
    }

    public Task<MediaScanResult> ScanAsync(
        string rootPath,
        IProgress<MediaScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            var normalizedRoot = NormalizeAndValidateRoot(rootPath);
            var warnings = new List<string>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };

            string[] filePaths;
            string[] directories;
            try
            {
                filePaths = Directory.EnumerateFiles(normalizedRoot, "*", options)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                directories = new[] { normalizedRoot }
                    .Concat(Directory.EnumerateDirectories(normalizedRoot, "*", options))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path.Length)
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new MediaScanException($"无法枚举素材目录：{error.Message}", error);
            }

            var items = new List<MediaItem>(filePaths.Length);
            for (var index = 0; index < filePaths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = filePaths[index];
                try
                {
                    var file = new FileInfo(path);
                    var id = await _fingerprintService.ComputeAsync(path, cancellationToken).ConfigureAwait(false);
                    var relativePath = Path.GetRelativePath(normalizedRoot, file.FullName);
                    var relativeFolder = Path.GetDirectoryName(relativePath) ?? "";
                    if (relativeFolder == ".")
                    {
                        relativeFolder = "";
                    }

                    items.Add(new MediaItem(
                        id,
                        file.FullName,
                        file.Name,
                        file.Extension.ToLowerInvariant(),
                        file.DirectoryName ?? normalizedRoot,
                        relativeFolder,
                        relativePath,
                        file.Length,
                        file.LastWriteTime,
                        file.LastWriteTimeUtc));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"跳过无法读取的文件：{Path.GetFileName(path)}（{error.Message}）");
                }

                progress?.Report(new MediaScanProgress(index + 1, filePaths.Length, Path.GetFileName(path)));
            }

            stopwatch.Stop();
            return new MediaScanResult(normalizedRoot, items, directories, warnings, stopwatch.Elapsed);
        }, cancellationToken);

    public static string NormalizeInputPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "";
        }

        var value = input.Trim();
        if (value.Length >= 2
            && ((value.StartsWith('"') && value.EndsWith('"'))
                || (value.StartsWith('\'') && value.EndsWith('\''))))
        {
            value = value[1..^1];
        }

        return Environment.ExpandEnvironmentVariables(value).Trim();
    }

    private static string NormalizeAndValidateRoot(string input)
    {
        var normalized = NormalizeInputPath(input);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new MediaScanException("请先输入素材路径。");
        }

        if (!Directory.Exists(normalized))
        {
            throw new MediaScanException($"素材路径不可访问：{normalized}");
        }

        var fullPath = Path.GetFullPath(normalized);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

public sealed class MediaScanException : Exception
{
    public MediaScanException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
