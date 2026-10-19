using System.IO;
using System.IO.Compression;

namespace OverByte;

/// <summary>UI-free folder operations (scan, compress &amp; enlarge, inflate every file).</summary>
internal static class FolderInflator
{
    static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Include hidden/system files like the old Directory.GetFiles did, but never follow symlinks or
        // junctions: a junction to a parent folder recursed endlessly, and a link to e.g. the home folder
        // pulled the whole home folder into the ZIP / output.
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple
    };

    public static string[] ListFiles(string folder) =>
        Directory.GetFiles(folder, "*", AllFiles);

    /// <summary>Total byte size and file count of a folder (inaccessible entries are skipped).</summary>
    public static (long total, int count) Scan(string folder, CancellationToken ct = default)
    {
        long total = 0; int count = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*", AllFiles))
        {
            ct.ThrowIfCancellationRequested();
            try { total += new FileInfo(f).Length; count++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return (total, count);
    }

    // ════════════════════════════════════════════════
    //  COMPRESS & ENLARGE: ZIP the folder, then inflate the ZIP
    // ════════════════════════════════════════════════

    public static void CompressEnlarge(string srcFolder, string outputZip, long targetBytes,
        IProgress<(double fraction, string status)> progress, CancellationToken ct)
    {
        if (Inflator.IsInsideOrSame(srcFolder, outputZip))
            throw new UserMessageException("The output ZIP must not be located inside the source folder.");

        var msg = Inflator.CheckDiskSpace(outputZip, targetBytes);
        if (msg != null) throw new UserMessageException(msg);

        var outFull = Path.GetFullPath(outputZip);
        var tmp = outFull + ".overbyte-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            // Step 1: Create ZIP (own loop so cancellation works between files)
            progress.Report((0, "compressing folder to ZIP..."));
            var files = ListFiles(srcFolder);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(srcFolder));
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var dir in Directory.GetDirectories(srcFolder, "*", AllFiles))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) continue; // only empty dirs need explicit entries
                    zip.CreateEntry(rel + "/");
                }
                for (int i = 0; i < files.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(root, files[i]).Replace(Path.DirectorySeparatorChar, '/');
                    progress.Report((0, $"compressing [{i + 1}/{files.Length}] {Path.GetFileName(files[i])}"));
                    zip.CreateEntryFromFile(files[i], rel, CompressionLevel.Optimal);
                }
            }
            ct.ThrowIfCancellationRequested();

            var zipSize = new FileInfo(tmp).Length;
            if (targetBytes <= zipSize)
            {
                // Keep the plain ZIP (matches old behaviour) but tell the user the target was too small.
                File.Move(tmp, outFull, overwrite: true);
                throw new UserMessageException(
                    $"Target ({Inflator.FormatSize(targetBytes)}) must exceed ZIP size ({Inflator.FormatSize(zipSize)}).");
            }

            long padding = targetBytes - zipSize;
            progress.Report((0, $"ZIP created ({Inflator.FormatSize(zipSize)}) — inflating..."));

            // Step 2: Add invisible padding (central-directory relocation technique), in place on the temp file
            Inflator.InflateZipBased(tmp, tmp, padding,
                new SyncProgress<(long written, string status)>(p =>
                    progress.Report(((double)p.written / padding,
                        $"inflating ZIP... {Inflator.FormatSize(p.written)} / {Inflator.FormatSize(padding)}"))),
                ct);
            ct.ThrowIfCancellationRequested();

            File.Move(tmp, outFull, overwrite: true);
        }
        catch
        {
            Inflator.TryDelete(tmp);
            throw;
        }
        progress.Report((1.0, "done"));
    }

    // ════════════════════════════════════════════════
    //  INFLATE ALL: every file individually
    // ════════════════════════════════════════════════

    public static int InflateAll(string srcFolder, string outputFolder, long targetPerFile,
        IProgress<(double fraction, string status)> progress, CancellationToken ct)
    {
        if (Inflator.IsInsideOrSame(srcFolder, outputFolder))
            throw new UserMessageException("The output folder must not be the source folder or located inside it.");

        var files = ListFiles(srcFolder);
        if (files.Length == 0) throw new UserMessageException("Folder is empty.");

        var sizes = new long[files.Length];
        long totalRequired = 0;
        for (int i = 0; i < files.Length; i++)
        {
            sizes[i] = new FileInfo(files[i]).Length;
            totalRequired += Math.Max(targetPerFile, sizes[i]);
            if (totalRequired < 0) totalRequired = long.MaxValue;
        }
        var msg = Inflator.CheckDiskSpace(outputFolder, totalRequired);
        if (msg != null) throw new UserMessageException(msg);

        var createdFiles = new List<string>();
        var createdDirs = new List<string>();
        try
        {
            EnsureDir(outputFolder, createdDirs);

            // Recreate empty directories too
            foreach (var d in Directory.GetDirectories(srcFolder, "*", AllFiles))
                EnsureDir(Path.Combine(outputFolder, Path.GetRelativePath(srcFolder, d)), createdDirs);

            int done = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var relPath = Path.GetRelativePath(srcFolder, file);
                var outPath = Path.Combine(outputFolder, relPath);
                EnsureDir(Path.GetDirectoryName(outPath), createdDirs);

                var fileSize = sizes[done];
                int idx = done;
                // Only files that did not exist before this run may be deleted on cancel/error.
                // (Previously a same-named file from an earlier run was overwritten and then deleted.)
                bool existedBefore = File.Exists(outPath);
                if (targetPerFile <= fileSize)
                {
                    // Just copy if already bigger than target
                    if (!existedBefore) createdFiles.Add(outPath);
                    File.Copy(file, outPath, overwrite: true);
                }
                else
                {
                    long padding = targetPerFile - fileSize;
                    var fileProgress = new SyncProgress<(long written, string status)>(p =>
                        progress.Report((((double)idx + (double)p.written / padding) / files.Length,
                            $"[{idx + 1}/{files.Length}] {Path.GetFileName(file)} — {Inflator.FormatSize(p.written)} / {Inflator.FormatSize(padding)}")));
                    if (!existedBefore) createdFiles.Add(outPath);
                    // InflateFile writes a temp file and renames it at the end, so a cancel here leaves an
                    // existing outPath untouched.
                    Inflator.InflateFile(file, outPath, padding, fileProgress, ct);
                }
                done++;
            }
            progress.Report((1.0, "done"));
            return files.Length;
        }
        catch
        {
            // Remove only what this run created — never files or folders that existed before it
            foreach (var f in createdFiles) Inflator.TryDelete(f);
            for (int i = createdDirs.Count - 1; i >= 0; i--)
            {
                try { if (!Directory.EnumerateFileSystemEntries(createdDirs[i]).Any()) Directory.Delete(createdDirs[i]); } catch { }
            }
            throw;
        }
    }

    static void EnsureDir(string? dir, List<string> created)
    {
        if (string.IsNullOrEmpty(dir) || Directory.Exists(dir)) return;
        var parent = Path.GetDirectoryName(dir);
        EnsureDir(parent, created);
        Directory.CreateDirectory(dir);
        created.Add(dir);
    }
}
