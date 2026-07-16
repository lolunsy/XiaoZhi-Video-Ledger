using System.Security.Cryptography;
using System.Text;

namespace XiaoZhiLedger.Core.Services;

public sealed class MediaFingerprintService
{
    private const int BufferSize = 65_536;

    public async Task<string> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            hash.AppendData(Encoding.UTF8.GetBytes($"{stream.Length}|"));

            var first = new byte[Math.Min(BufferSize, checked((int)Math.Min(stream.Length, BufferSize)))];
            if (first.Length > 0)
            {
                var firstRead = await ReadUpToAsync(stream, first, cancellationToken).ConfigureAwait(false);
                hash.AppendData(first.AsSpan(0, firstRead));
            }

            if (stream.Length > BufferSize)
            {
                stream.Seek(Math.Max(0, stream.Length - BufferSize), SeekOrigin.Begin);
                var last = new byte[BufferSize];
                var lastRead = await ReadUpToAsync(stream, last, cancellationToken).ConfigureAwait(false);
                hash.AppendData(last.AsSpan(0, lastRead));
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            var fallback = SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
            return Convert.ToHexString(fallback).ToLowerInvariant();
        }
    }

    private static async Task<int> ReadUpToAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
