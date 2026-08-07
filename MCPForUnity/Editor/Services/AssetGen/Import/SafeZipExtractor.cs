using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace MCPForUnity.Editor.Services.AssetGen.Import
{
    /// <summary>
    /// Extracts a .zip into a destination directory while rejecting Zip-Slip path traversal:
    /// every entry's resolved target must stay inside <c>destDir</c>. Directory entries are
    /// created; file entries are written by copying the entry stream (no reliance on the
    /// ZipFileExtensions helper). Used to unpack marketplace model archives (e.g. Sketchfab).
    ///
    /// When <paramref name="allowedExtensions"/> is supplied, file entries whose extension is not
    /// on the allowlist are SKIPPED (not written). Callers that extract UNTRUSTED archives into the
    /// Assets tree MUST pass an allowlist of inert asset types so executable content (.cs/.dll/
    /// .asmdef) can never land under Assets/ and be compiled/loaded by the Editor.
    /// </summary>
    public static class SafeZipExtractor
    {
        public static void ExtractTo(
            string zipPath,
            string destDir,
            ISet<string> allowedExtensions = null,
            int maxEntries = 2048,
            long maxEntryUncompressedBytes = 512L * 1024L * 1024L,
            long maxTotalUncompressedBytes = 1024L * 1024L * 1024L,
            double maxCompressionRatio = 200.0)
        {
            if (string.IsNullOrEmpty(zipPath)) throw new ArgumentException("zipPath required", nameof(zipPath));
            if (string.IsNullOrEmpty(destDir)) throw new ArgumentException("destDir required", nameof(destDir));
            if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            if (maxEntryUncompressedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntryUncompressedBytes));
            if (maxTotalUncompressedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalUncompressedBytes));
            if (maxCompressionRatio <= 0) throw new ArgumentOutOfRangeException(nameof(maxCompressionRatio));

            Directory.CreateDirectory(destDir);
            string destFull = Path.GetFullPath(destDir);
            string prefix = destFull.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? destFull
                : destFull + Path.DirectorySeparatorChar;

            using (FileStream fs = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count > maxEntries)
                    throw new IOException($"Zip contains {archive.Entries.Count} entries; limit is {maxEntries}.");

                long declaredTotal = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.Length > maxEntryUncompressedBytes)
                        throw new IOException($"Zip entry exceeds the uncompressed size limit: {entry.FullName}");
                    declaredTotal = checked(declaredTotal + entry.Length);
                    if (declaredTotal > maxTotalUncompressedBytes)
                        throw new IOException("Zip exceeds the total uncompressed size limit.");
                    if (entry.Length > 0
                        && (entry.CompressedLength <= 0
                            || entry.Length / (double)entry.CompressedLength > maxCompressionRatio))
                    {
                        throw new IOException($"Zip entry exceeds the compression-ratio limit: {entry.FullName}");
                    }
                }

                long totalWritten = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = entry.FullName;
                    if (string.IsNullOrEmpty(name)) continue;

                    // Reject traversal / absolute paths up front.
                    if (name.Contains("..") || Path.IsPathRooted(name))
                        throw new IOException($"Unsafe zip entry rejected: {name}");

                    string target = Path.GetFullPath(Path.Combine(destDir, name));
                    if (!target.StartsWith(prefix, StringComparison.Ordinal))
                        throw new IOException($"Unsafe zip entry escapes destination: {name}");

                    // A directory entry has an empty Name (FullName ends with a separator).
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    // Allowlist gate: skip anything that isn't an inert asset type the caller permits.
                    if (allowedExtensions != null && allowedExtensions.Count > 0
                        && !allowedExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant()))
                    {
                        continue;
                    }

                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    using (Stream src = entry.Open())
                    using (FileStream dst = File.Create(target))
                    {
                        var buffer = new byte[81920];
                        long entryWritten = 0;
                        int read;
                        try
                        {
                            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                entryWritten += read;
                                totalWritten += read;
                                if (entryWritten > maxEntryUncompressedBytes
                                    || totalWritten > maxTotalUncompressedBytes)
                                {
                                    throw new IOException("Zip expanded beyond the configured extraction limit.");
                                }
                                dst.Write(buffer, 0, read);
                            }
                        }
                        catch
                        {
                            dst.Close();
                            try { File.Delete(target); } catch { }
                            throw;
                        }
                    }
                }
            }
        }
    }
}
