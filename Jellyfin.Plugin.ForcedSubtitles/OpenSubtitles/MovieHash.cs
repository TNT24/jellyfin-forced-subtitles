using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.ForcedSubtitles.OpenSubtitles;

/// <summary>
/// The OpenSubtitles file hash: file size plus the 64-bit little-endian word sums of the first and last 64 KiB.
/// A hash match means the subtitle was timed against this exact release.
/// </summary>
public static class MovieHash
{
    private const int ChunkSize = 64 * 1024;

    /// <summary>Computes the hash, or returns null if the file is missing or too small.</summary>
    /// <param name="path">Path to the video file.</param>
    /// <returns>16 lowercase hex characters, or null.</returns>
    public static string? TryCompute(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = stream.Length;
            if (length < ChunkSize)
            {
                return null;
            }

            ulong hash = (ulong)length;
            var buffer = new byte[ChunkSize];

            stream.ReadExactly(buffer);
            hash = SumChunk(hash, buffer);

            stream.Seek(-ChunkSize, SeekOrigin.End);
            stream.ReadExactly(buffer);
            hash = SumChunk(hash, buffer);

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ulong SumChunk(ulong hash, ReadOnlySpan<byte> chunk)
    {
        unchecked
        {
            for (int i = 0; i < chunk.Length; i += 8)
            {
                hash += BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(i, 8));
            }
        }

        return hash;
    }
}
