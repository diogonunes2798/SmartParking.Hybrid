using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SmartParking.Application.Retention;

/// <summary>
/// Archive of one UTC day as newline-delimited JSON compressed with gzip (ADR-0013):
/// <c>{ArchivePath}/occupancy-events-YYYY-MM-DD.ndjson.gz</c>. gzip because it is in the .NET base library (zstd is not
/// in .NET 10) and every tool reads it. The file is written to <c>*.partial</c>, flushed to disk and renamed, so a crash
/// never leaves a truncated file under the final name; the SHA-256 of the compressed bytes goes to the manifest.
/// Only base library types: no storage driver in the Application layer.
/// </summary>
public sealed class GzipNdjsonEventArchive(RetentionOptions options) : IEventArchive
{
    public async Task<ArchiveFile> WriteDayAsync(DateOnly day, IAsyncEnumerable<string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var path = PathOf(day);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var partial = path + ".partial";

        long count = 0;
        var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true);
            await using (gzip.ConfigureAwait(false))
            {
                var writer = new StreamWriter(gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 16);
                await using (writer.ConfigureAwait(false))
                {
                    await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await writer.WriteAsync('\n').ConfigureAwait(false);
                        count++;
                    }
                }
            }

            file.Flush(flushToDisk: true);
        }

        File.Move(partial, path, overwrite: true);

        string sha256;
        long bytes;
        var read = File.OpenRead(path);
        await using (read.ConfigureAwait(false))
        {
            bytes = read.Length;
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, cancellationToken).ConfigureAwait(false));
        }

        return new ArchiveFile(path, count, bytes, sha256);
    }

    public void Delete(DateOnly day) => File.Delete(PathOf(day));

    /// <summary>Full path of the archive file of <paramref name="day"/>.</summary>
    public string PathOf(DateOnly day) => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        options.ArchivePath, string.Create(CultureInfo.InvariantCulture, $"occupancy-events-{day:yyyy-MM-dd}.ndjson.gz")));
}
