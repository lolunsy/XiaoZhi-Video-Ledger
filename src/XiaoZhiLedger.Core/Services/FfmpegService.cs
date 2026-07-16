using System.Diagnostics;
using System.Globalization;
using XiaoZhiLedger.Core.Models;

namespace XiaoZhiLedger.Core.Services;

public sealed class FfmpegService
{
    public FfmpegToolset? ResolveToolset(string appDirectory, string configuredPath)
    {
        var candidates = new List<string>
        {
            Path.Combine(appDirectory, "tools", "ffmpeg.exe")
        };
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        var pathCommand = FindOnPath("ffmpeg.exe");
        if (pathCommand is not null)
        {
            candidates.Add(pathCommand);
        }

        foreach (var ffmpeg in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(ffmpeg))
            {
                continue;
            }

            var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
            if (!File.Exists(ffprobe))
            {
                ffprobe = FindOnPath("ffprobe.exe") ?? "";
            }

            return new FfmpegToolset(ffmpeg, File.Exists(ffprobe) ? ffprobe : null);
        }

        return null;
    }

    public async Task<double> GetDurationAsync(
        FfmpegToolset tools,
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        if (tools.FfprobePath is null)
        {
            return 12;
        }

        var result = await RunAsync(
            tools.FfprobePath,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", mediaPath],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
               && double.TryParse(result.StandardOutput.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var duration)
               && duration > 0.1
            ? duration
            : 12;
    }

    public async Task GenerateThumbnailPairAsync(
        FfmpegToolset tools,
        MediaItem item,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (IsUsableOutput(outputPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = CreateTemporaryOutputPath(outputPath);
        var duration = await GetDurationAsync(tools, item.Path, cancellationToken).ConfigureAwait(false);
        var timeA = Math.Max(0.15, duration * 0.24);
        var timeB = Math.Min(Math.Max(0.2, duration - 0.12), Math.Max(timeA + 0.12, duration * 0.68));
        const string filter = "[0:v]scale=138:246:force_original_aspect_ratio=decrease," +
                              "pad=138:246:(ow-iw)/2:(oh-ih)/2:color=black[a];" +
                              "[1:v]scale=138:246:force_original_aspect_ratio=decrease," +
                              "pad=138:246:(ow-iw)/2:(oh-ih)/2:color=black[b];[a][b]hstack=inputs=2";
        try
        {
            var result = await RunAsync(
                tools.FfmpegPath,
                [
                    "-y", "-hide_banner", "-loglevel", "error",
                    "-ss", FormatTime(timeA), "-i", item.Path,
                    "-ss", FormatTime(timeB), "-i", item.Path,
                    "-filter_complex", filter, "-frames:v", "1", "-q:v", "3", temporaryPath
                ],
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            CompleteGeneratedOutput(result, temporaryPath, outputPath, "代表帧");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public async Task GenerateScrubSpriteAsync(
        FfmpegToolset tools,
        MediaItem item,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (IsUsableOutput(outputPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = CreateTemporaryOutputPath(outputPath);
        var duration = await GetDurationAsync(tools, item.Path, cancellationToken).ConfigureAwait(false);
        var window = Math.Clamp(duration / 8, 0.35, 1.0);
        var maxStart = Math.Max(0, duration - window);
        var starts = new[] { 0.05, 0.33, 0.61, 0.89 }
            .Select(fraction => maxStart * fraction)
            .ToArray();
        var fps = 4 / window;
        var segments = Enumerable.Range(0, 4)
            .Select(index =>
                $"[{index}:v]fps={fps.ToString("0.########", CultureInfo.InvariantCulture)}," +
                "scale=180:320:force_original_aspect_ratio=decrease," +
                $"pad=180:320:(ow-iw)/2:(oh-ih)/2:color=black,trim=end_frame=4,setpts=PTS-STARTPTS[s{index}]");
        var filter = string.Join(";", segments) +
                     ";[s0][s1][s2][s3]concat=n=4:v=1:a=0,tile=4x4:nb_frames=16:padding=0:margin=0";
        var arguments = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        foreach (var start in starts)
        {
            arguments.AddRange(["-ss", FormatTime(start), "-t", FormatTime(window), "-i", item.Path]);
        }
        arguments.AddRange(["-filter_complex", filter, "-frames:v", "1", "-q:v", "4", temporaryPath]);
        try
        {
            var result = await RunAsync(
                tools.FfmpegPath,
                arguments,
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            CompleteGeneratedOutput(result, temporaryPath, outputPath, "扫片缓存");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public async Task GenerateProxyAsync(
        FfmpegToolset tools,
        MediaItem item,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        if (IsUsableOutput(outputPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = CreateTemporaryOutputPath(outputPath);
        try
        {
            var result = await RunAsync(
                tools.FfmpegPath,
                [
                    "-y", "-hide_banner", "-loglevel", "error", "-i", item.Path,
                    "-map", "0:v:0", "-map", "0:a:0?",
                    "-vf", "scale=960:-2:force_original_aspect_ratio=decrease",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "29",
                    "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                    "-movflags", "+faststart", temporaryPath
                ],
                TimeSpan.FromMinutes(10),
                cancellationToken).ConfigureAwait(false);
            CompleteGeneratedOutput(result, temporaryPath, outputPath, "兼容预览");
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
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

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup.
            }

            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Path.GetFileName(executable)} 运行超时。");
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    private static void CompleteGeneratedOutput(
        ProcessResult result,
        string temporaryPath,
        string outputPath,
        string operation)
    {
        if (result.ExitCode == 0 && IsUsableOutput(temporaryPath))
        {
            File.Move(temporaryPath, outputPath, overwrite: true);
            return;
        }

        TryDelete(temporaryPath);

        var error = result.StandardError.Trim();
        if (error.Length > 400)
        {
            error = error[..400];
        }

        throw new InvalidOperationException($"{operation}生成失败（退出码 {result.ExitCode}）：{error}");
    }

    private static bool IsUsableOutput(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static string CreateTemporaryOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var extension = Path.GetExtension(outputPath);
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $"{stem}.partial-{Guid.NewGuid():N}{extension}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache cleanup is best effort.
        }
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string FormatTime(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record FfmpegToolset(string FfmpegPath, string? FfprobePath);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
