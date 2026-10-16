using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OverByte;

/// <summary>Error whose message is meant to be shown to the user as-is (not a crash).</summary>
internal sealed class UserMessageException : Exception
{
    public UserMessageException(string message) : base(message) { }
}

/// <summary>IProgress that invokes the callback synchronously on the calling thread (no SynchronizationContext hop).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    readonly Action<T> _action;
    public SyncProgress(Action<T> action) { _action = action; }
    public void Report(T value) => _action(value);
}

/// <summary>
/// UI-free inflation engine shared by the Windows (WinForms) and Linux (Avalonia) front-ends.
/// Every public entry point produces an output file whose size is exactly
/// (source size + paddingBytes) and which remains a valid file of its format.
/// </summary>
internal static class Inflator
{
    const int BufSize = 1024 * 1024;

    // ════════════════════════════════════════════════
    //  FORMAT ROUTER
    // ════════════════════════════════════════════════

    /// <summary>
    /// Inflates <paramref name="source"/> into <paramref name="output"/>. Output is always written to a
    /// temporary sibling file first and renamed on success, so (a) overwriting the source in place is safe,
    /// and (b) cancellation / errors never leave a partial or corrupted file behind.
    /// </summary>
    public static void InflateFile(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (paddingBytes < 0) throw new ArgumentOutOfRangeException(nameof(paddingBytes));
        var dstFull = Path.GetFullPath(output);
        var tmp = dstFull + ".overbyte-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            InflateFileTo(source, tmp, paddingBytes, progress, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, dstFull, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    static void InflateFileTo(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(source).ToLowerInvariant();

        if (ext == ".png" && HasPngLayout(source))
            InflatePng(source, output, paddingBytes, progress, ct);
        else if (ext is ".jpg" or ".jpeg" or ".jfif" && HasJpegLayout(source))
            InflateJpeg(source, output, paddingBytes, progress, ct);
        else if (ext is ".mp4" or ".mov" or ".m4v" or ".m4a" or ".3gp" or ".f4v")
            InflateMp4(source, output, paddingBytes, progress, ct);
        else if (ext is ".zip" or ".pptx" or ".docx" or ".xlsx" or ".odt" or ".ods" or ".odp"
                 or ".jar" or ".apk" or ".xpi" or ".epub")
            InflateZipBased(source, output, paddingBytes, progress, ct);
        else if (ext == ".pdf")
            InflatePdf(source, output, paddingBytes, progress, ct);
        else
            InflateGeneric(source, output, paddingBytes, progress, ct);
    }

    // ════════════════════════════════════════════════
    //  PNG: Hybrid approach.
    //  Phase 1: paDd chunks before IEND (up to 2.5GB)
    //           — WIC processes these correctly.
    //  Phase 2: Raw zeros appended AFTER IEND for the
    //           rest — WIC stops at IEND, trailing data
    //           is post-EOF and ignored.
    //  Image loads fully before hitting any padding.
    //  Chunk framing (12 bytes per chunk) is counted in
    //  the padding so the file hits the target exactly.
    // ════════════════════════════════════════════════

    const long MaxStructuredPadding = 2_500_000_000L; // 2.5GB safe zone for WIC

    static readonly byte[] PngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    static readonly byte[] PngIend = { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

    /// <summary>True if the file is a PNG whose very last 12 bytes are the IEND chunk (no trailing data).</summary>
    static bool HasPngLayout(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length < 8 + 12) return false;
            var head = new byte[8]; f.ReadExactly(head, 0, 8);
            if (!head.AsSpan().SequenceEqual(PngSig)) return false;
            f.Seek(-12, SeekOrigin.End);
            var tail = new byte[12]; f.ReadExactly(tail, 0, 12);
            return tail.AsSpan().SequenceEqual(PngIend);
        }
        catch { return false; }
    }

    static void InflatePng(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufSize);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, BufSize);

        long srcLen = src.Length;
        long structuredBytes = Math.Min(paddingBytes, MaxStructuredPadding);
        long trailingBytes = paddingBytes - structuredBytes;

        // Copy everything EXCEPT the last 12 bytes (IEND chunk)
        CopyStream(src, dst, srcLen - 12, ct);

        // Phase 1: paDd chunks before IEND (up to 2.5GB)
        long budget = structuredBytes; // counts the full chunk: 4 len + 4 type + data + 4 crc
        long totalWritten = 0;
        const int overhead = 12;
        const int maxChunkData = 16 * 1024 * 1024; // 16MB per chunk — small enough for WIC, large enough for speed
        byte[] chunkType = "paDd"u8.ToArray();
        byte[] zeros = new byte[BufSize];
        int cachedLen = -1; uint cachedCrc = 0; // every chunk of equal length has the same CRC (data is all zeros)

        while (budget >= overhead)
        {
            ct.ThrowIfCancellationRequested();
            long total = Math.Min(budget, maxChunkData + (long)overhead);
            long left = budget - total;
            if (left > 0 && left < overhead) total -= (overhead - left); // never leave a remainder too small for a chunk
            int chunkDataSize = (int)(total - overhead);

            WriteBE32(dst, chunkDataSize);
            dst.Write(chunkType, 0, 4);

            if (chunkDataSize != cachedLen)
            {
                uint crc = Crc32Png.Init();
                crc = Crc32Png.Update(crc, chunkType);
                int n = chunkDataSize;
                while (n > 0)
                {
                    int s = Math.Min(zeros.Length, n);
                    crc = Crc32Png.Update(crc, zeros.AsSpan(0, s));
                    n -= s;
                }
                cachedCrc = Crc32Png.Final(crc);
                cachedLen = chunkDataSize;
            }

            int dataLeft = chunkDataSize;
            while (dataLeft > 0)
            {
                ct.ThrowIfCancellationRequested();
                int toWrite = Math.Min(zeros.Length, dataLeft);
                dst.Write(zeros, 0, toWrite);
                dataLeft -= toWrite;
                totalWritten += toWrite;
                if (totalWritten % (1024 * 1024 * 4) == 0)
                    progress.Report((totalWritten, ""));
            }

            WriteBE32(dst, (int)cachedCrc);
            totalWritten += overhead;
            budget -= total;
            progress.Report((Math.Min(totalWritten, paddingBytes), ""));
        }
        trailingBytes += budget; // < 12 leftover bytes go after IEND
        long structuredDone = structuredBytes - budget;

        // Write original IEND chunk (last 12 bytes)
        src.Seek(-12, SeekOrigin.End);
        byte[] iend = new byte[12];
        src.ReadExactly(iend, 0, 12);
        dst.Write(iend, 0, 12);

        // Phase 2: Raw zeros after IEND (WIC stops at IEND, ignores trailing)
        if (trailingBytes > 0)
        {
            var trailProgress = new SyncProgress<(long, string)>(p =>
                progress.Report((structuredDone + p.Item1, "")));
            WritePaddingRaw(dst, trailingBytes, trailProgress, ct);
        }

        dst.Flush();
        progress.Report((paddingBytes, ""));
    }

    // ════════════════════════════════════════════════
    //  JPEG: Hybrid approach.
    //  Phase 1: COM markers before EOI (up to 2.5GB)
    //           — WIC processes these correctly.
    //  Phase 2: Raw zeros appended AFTER EOI for the
    //           rest — WIC stops at EOI, trailing data
    //           is post-EOF and ignored.
    //  Image decoded fully BEFORE any padding.
    //  COM data = 0x20 (space), valid ISO 8859-1.
    //  Each COM segment is at most 65535 bytes of length
    //  field (65533 data) and costs 4 bytes of framing,
    //  which is counted so the target is hit exactly.
    // ════════════════════════════════════════════════

    /// <summary>True if the file starts with SOI (FF D8) and ends exactly with EOI (FF D9).</summary>
    static bool HasJpegLayout(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length < 4) return false;
            var b = new byte[2]; f.ReadExactly(b, 0, 2);
            if (b[0] != 0xFF || b[1] != 0xD8) return false;
            f.Seek(-2, SeekOrigin.End); f.ReadExactly(b, 0, 2);
            return b[0] == 0xFF && b[1] == 0xD9;
        }
        catch { return false; }
    }

    static void InflateJpeg(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufSize);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, BufSize);

        long srcLen = src.Length;
        long structuredBytes = Math.Min(paddingBytes, MaxStructuredPadding);
        long trailingBytes = paddingBytes - structuredBytes;

        // Copy everything EXCEPT the last 2 bytes (EOI: FF D9)
        CopyStream(src, dst, srcLen - 2, ct);

        // Phase 1: COM markers before EOI (up to 2.5GB)
        const int overhead = 4;          // FF FE + 2-byte length
        const int maxComData = 65533;    // length field (data + 2) must fit in 16 bits
        byte[] spaceBuf = new byte[maxComData];
        Array.Fill(spaceBuf, (byte)0x20);
        long budget = structuredBytes;
        long totalWritten = 0;
        long reportAt = 0;

        while (budget >= overhead)
        {
            ct.ThrowIfCancellationRequested();
            long total = Math.Min(budget, maxComData + (long)overhead);
            long left = budget - total;
            if (left > 0 && left < overhead) total -= (overhead - left);
            int dataSize = (int)(total - overhead);
            int segLength = dataSize + 2;

            dst.WriteByte(0xFF);
            dst.WriteByte(0xFE);
            dst.WriteByte((byte)(segLength >> 8));
            dst.WriteByte((byte)(segLength & 0xFF));
            dst.Write(spaceBuf, 0, dataSize);

            budget -= total;
            totalWritten += total;
            if (totalWritten - reportAt >= 4L * 1024 * 1024 || budget < overhead)
            {
                reportAt = totalWritten;
                progress.Report((Math.Min(totalWritten, paddingBytes), ""));
            }
        }
        trailingBytes += budget; // < 4 leftover bytes go after EOI
        long structuredDone = structuredBytes - budget;

        // Write original EOI (last 2 bytes: FF D9)
        src.Seek(-2, SeekOrigin.End);
        byte[] eoi = new byte[2];
        src.ReadExactly(eoi, 0, 2);
        dst.Write(eoi, 0, 2);

        // Phase 2: Raw zeros after EOI (WIC stops at EOI, ignores trailing)
        if (trailingBytes > 0)
        {
            var trailProgress = new SyncProgress<(long, string)>(p =>
                progress.Report((structuredDone + p.Item1, "")));
            WritePaddingRaw(dst, trailingBytes, trailProgress, ct);
        }

        dst.Flush();
        progress.Report((paddingBytes, ""));
    }

    // Stream copy helper — copies exactly 'count' bytes without loading all into RAM
    static void CopyStream(Stream src, Stream dst, long count, CancellationToken ct)
    {
        byte[] buf = new byte[BufSize];
        long left = count;
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buf.Length, left);
            int read = src.Read(buf, 0, toRead);
            if (read == 0) throw new EndOfStreamException("Source file ended unexpectedly (was it modified while reading?).");
            dst.Write(buf, 0, read);
            left -= read;
        }
    }

    // ════════════════════════════════════════════════
    //  MP4/MOV: Append a "free" box (ISO 14496-12 standard)
    //  All MP4 parsers skip free/skip boxes by spec.
    //  The box size covers its own header, so the file
    //  grows by exactly paddingBytes. A 32-bit size is
    //  used when it fits, otherwise the 64-bit largesize.
    // ════════════════════════════════════════════════

    static void InflateMp4(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!SamePath(source, output))
            File.Copy(source, output, overwrite: true);

        // A box needs at least an 8-byte header; anything smaller cannot be expressed as a box.
        if (paddingBytes < 8)
        {
            AppendZeros(output, paddingBytes, progress, ct);
            return;
        }

        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, BufSize);
        fs.Seek(0, SeekOrigin.End);

        if (paddingBytes <= uint.MaxValue)
        {
            WriteBE32(fs, (int)(uint)paddingBytes);                 // 32-bit box size (includes 8-byte header)
            fs.Write("free"u8.ToArray(), 0, 4);
            WritePaddingRaw(fs, paddingBytes - 8, progress, ct);
        }
        else
        {
            WriteBE32(fs, 1);                                       // size=1 means "use 64-bit largesize"
            fs.Write("free"u8.ToArray(), 0, 4);
            WriteBE64(fs, paddingBytes);                            // largesize (includes 16-byte header)
            WritePaddingRaw(fs, paddingBytes - 16, progress, ct);
        }
        progress.Report((paddingBytes, ""));
    }

    // ════════════════════════════════════════════════
    //  PDF: Append as a PDF stream object + incremental
    //  update (xref table, or xref stream when the file
    //  uses one). The new trailer carries /Root, /Prev,
    //  /Info, /ID and /Encrypt from the previous one, and
    //  the stream length is solved so the file grows by
    //  exactly paddingBytes.
    // ════════════════════════════════════════════════

    sealed class PdfTrailerInfo
    {
        public long Prev;
        public int Size;
        public string Root = "";
        public string? Info, Id, Encrypt;
        public bool IsXrefStream;
    }

    static PdfTrailerInfo? ReadPdfTrailerInfo(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length;
            int tailLen = (int)Math.Min(len, 2048);
            var tailBytes = new byte[tailLen];
            fs.Seek(-tailLen, SeekOrigin.End);
            fs.ReadExactly(tailBytes, 0, tailLen);
            string tail = Encoding.Latin1.GetString(tailBytes);

            int sx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
            if (sx < 0) return null;
            var m = Regex.Match(tail.Substring(sx + 9), @"^\s*(\d+)");
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long prev)) return null;
            if (prev <= 0 || prev >= len) return null;

            int n = (int)Math.Min(len - prev, 16L * 1024 * 1024);
            var buf = new byte[n];
            fs.Seek(prev, SeekOrigin.Begin);
            fs.ReadExactly(buf, 0, n);
            string t = Encoding.Latin1.GetString(buf);

            string dict;
            bool isStream;
            if (Regex.IsMatch(t, @"^\s*xref"))
            {
                int ti = t.IndexOf("trailer", StringComparison.Ordinal);
                if (ti < 0) return null;
                int end = t.IndexOf("startxref", ti, StringComparison.Ordinal);
                dict = end < 0 ? t.Substring(ti) : t.Substring(ti, end - ti);
                isStream = false;
            }
            else if (Regex.IsMatch(t, @"^\s*\d+\s+\d+\s+obj"))
            {
                int si = t.IndexOf("stream", StringComparison.Ordinal);
                dict = si < 0 ? t : t.Substring(0, si);
                isStream = true;
            }
            else return null;

            var size = Regex.Match(dict, @"/Size\s+(\d+)");
            var root = Regex.Match(dict, @"/Root\s+(\d+\s+\d+\s+R)");
            if (!size.Success || !root.Success || !int.TryParse(size.Groups[1].Value, out int sz)) return null;
            if (Regex.IsMatch(dict, @"/Encrypt\s*<<")) return null; // inline encrypt dict: don't risk it

            var info = Regex.Match(dict, @"/Info\s+(\d+\s+\d+\s+R)");
            var id = Regex.Match(dict, @"/ID\s*(\[\s*<[0-9A-Fa-f\s]*>\s*<[0-9A-Fa-f\s]*>\s*\])");
            var enc = Regex.Match(dict, @"/Encrypt\s+(\d+\s+\d+\s+R)");
            return new PdfTrailerInfo
            {
                Prev = prev, Size = sz, Root = root.Groups[1].Value, IsXrefStream = isStream,
                Info = info.Success ? info.Groups[1].Value : null,
                Id = id.Success ? id.Groups[1].Value : null,
                Encrypt = enc.Success ? enc.Groups[1].Value : null
            };
        }
        catch { return null; }
    }

    static (byte[] head, byte[] tail) BuildPdfUpdate(PdfTrailerInfo p, long startPos, long streamLen)
    {
        int objNum = p.Size;
        long objOffset = startPos + 1;
        string head = $"\n{objNum} 0 obj\n<< /Length {streamLen} >>\nstream\n";
        string mid = "\nendstream\nendobj\n";
        long xrefPos = startPos + head.Length + streamLen + mid.Length;

        string common = $" /Root {p.Root}"
            + (p.Info != null ? $" /Info {p.Info}" : "")
            + (p.Id != null ? $" /ID {p.Id}" : "")
            + (p.Encrypt != null ? $" /Encrypt {p.Encrypt}" : "")
            + $" /Prev {p.Prev}";

        using var tail = new MemoryStream();
        void Add(string s) { var b = Encoding.Latin1.GetBytes(s); tail.Write(b, 0, b.Length); }
        Add(mid);
        if (!p.IsXrefStream)
        {
            Add($"xref\n0 1\n0000000000 65535 f \n{objNum} 1\n{objOffset:D10} 00000 n \n" +
                $"trailer\n<< /Size {objNum + 1}{common} >>\nstartxref\n{xrefPos}\n%%EOF\n");
        }
        else
        {
            int xrefObj = objNum + 1;
            Add($"{xrefObj} 0 obj\n<< /Type /XRef /Size {objNum + 2} /W [1 8 2] /Index [{objNum} 2]{common} /Length 22 >>\nstream\n");
            void Entry(long off) { tail.WriteByte(1); WriteBE64(tail, off); tail.WriteByte(0); tail.WriteByte(0); }
            Entry(objOffset);
            Entry(xrefPos);
            Add($"\nendstream\nendobj\nstartxref\n{xrefPos}\n%%EOF\n");
        }
        return (Encoding.Latin1.GetBytes(head), tail.ToArray());
    }

    static void InflatePdf(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!SamePath(source, output))
            File.Copy(source, output, overwrite: true);

        var info = ReadPdfTrailerInfo(output);
        long startPos = new FileInfo(output).Length;
        if (info == null || paddingBytes < 600 || startPos >= 9_000_000_000L)
        {
            // Can't build a trustworthy incremental update — append raw zeros instead.
            AppendZeros(output, paddingBytes, progress, ct);
            return;
        }

        // Solve the stream length so that head + stream + tail == paddingBytes.
        long s = Math.Max(0, paddingBytes - 400);
        (byte[] head, byte[] tail) = BuildPdfUpdate(info, startPos, s);
        long total = head.Length + s + tail.Length;
        for (int i = 0; i < 8 && total != paddingBytes; i++)
        {
            s += paddingBytes - total;
            if (s < 0) break;
            (head, tail) = BuildPdfUpdate(info, startPos, s);
            total = head.Length + s + tail.Length;
        }
        if (total > paddingBytes && s >= 0)
        {
            s -= total - paddingBytes;
            if (s >= 0)
            {
                (head, tail) = BuildPdfUpdate(info, startPos, s);
                total = head.Length + s + tail.Length;
            }
        }
        if (s < 0 || total > paddingBytes)
        {
            AppendZeros(output, paddingBytes, progress, ct);
            return;
        }
        long extra = paddingBytes - total; // only non-zero at digit-count boundaries; filled with newlines after %%EOF

        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, BufSize);
        fs.Seek(0, SeekOrigin.End);
        fs.Write(head, 0, head.Length);
        WritePaddingRaw(fs, s, progress, ct);
        fs.Write(tail, 0, tail.Length);
        for (long i = 0; i < extra; i++) fs.WriteByte((byte)'\n');
        fs.Flush();
        progress.Report((paddingBytes, ""));
    }

    // ════════════════════════════════════════════════
    //  ZIP-based: insert the padding between the last
    //  local file entry and the central directory, then
    //  patch the central-directory offset in the EOCD
    //  (and ZIP64 EOCD / locator when present, or create
    //  them when the new offset no longer fits in 32 bits).
    //  Local-header offsets in the central directory are
    //  untouched because no local entry moves. Readers
    //  that locate the archive from the end of the file
    //  (unzip, Python zipfile, .NET, 7-Zip, Java) see a
    //  perfectly normal archive with no extra entries.
    // ════════════════════════════════════════════════

    internal static void InflateZipBased(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!SamePath(source, output))
            File.Copy(source, output, overwrite: true);

        if (!TryInflateZipStructured(output, paddingBytes, progress, ct))
            AppendZeros(output, paddingBytes, progress, ct); // not a zip we understand: generic append
    }

    static bool TryInflateZipStructured(string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        const int maxTail = 256 * 1024 * 1024;
        long fileLen;
        byte[] eocdScan;
        long scanStart;
        using (var r = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fileLen = r.Length;
            // max comment = 65535 + 22 byte EOCD
            int tailSize = (int)Math.Min(fileLen, 65557);
            scanStart = fileLen - tailSize;
            eocdScan = new byte[tailSize];
            r.Seek(scanStart, SeekOrigin.Begin);
            r.ReadExactly(eocdScan, 0, tailSize);
        }

        // Find EOCD signature (0x50 0x4B 0x05 0x06) scanning backward; the comment length must reach EOF exactly.
        int eocdIdx = -1;
        for (int i = eocdScan.Length - 22; i >= 0; i--)
        {
            if (eocdScan[i] == 0x50 && eocdScan[i + 1] == 0x4B && eocdScan[i + 2] == 0x05 && eocdScan[i + 3] == 0x06)
            {
                int commentLen = eocdScan[i + 20] | (eocdScan[i + 21] << 8);
                if (i + 22 + commentLen == eocdScan.Length) { eocdIdx = i; break; }
            }
        }
        if (eocdIdx < 0) return false;
        long eocdPos = scanStart + eocdIdx;

        uint cdSize32 = ReadU32(eocdScan, eocdIdx + 12);
        uint cdOff32 = ReadU32(eocdScan, eocdIdx + 16);
        long cdSize = cdSize32, cdOff = cdOff32;

        // ZIP64 locator directly before EOCD?
        long z64RecPos = -1;
        if (eocdPos >= 20)
        {
            var loc = new byte[20];
            using (var r = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                r.Seek(eocdPos - 20, SeekOrigin.Begin);
                r.ReadExactly(loc, 0, 20);
            }
            if (loc[0] == 0x50 && loc[1] == 0x4B && loc[2] == 0x06 && loc[3] == 0x07)
            {
                z64RecPos = (long)ReadU64(loc, 8);
                if (z64RecPos < 0 || z64RecPos + 56 > eocdPos - 20) return false;
                var rec = new byte[56];
                using (var r = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    r.Seek(z64RecPos, SeekOrigin.Begin);
                    r.ReadExactly(rec, 0, 56);
                }
                if (!(rec[0] == 0x50 && rec[1] == 0x4B && rec[2] == 0x06 && rec[3] == 0x06)) return false;
                if (cdSize32 == uint.MaxValue) cdSize = (long)ReadU64(rec, 40);
                if (cdOff32 == uint.MaxValue) cdOff = (long)ReadU64(rec, 48);
            }
        }

        // Sanity: central directory must be where the EOCD says it is.
        long cdLimit = z64RecPos >= 0 ? z64RecPos : eocdPos;
        if (cdOff < 0 || cdSize < 0 || cdOff + cdSize > cdLimit) return false;
        if (fileLen - cdOff > maxTail) return false;

        byte[] tail = new byte[(int)(fileLen - cdOff)];
        using (var r = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            r.Seek(cdOff, SeekOrigin.Begin);
            r.ReadExactly(tail, 0, tail.Length);
        }
        if (cdSize > 0 && !(tail[0] == 0x50 && tail[1] == 0x4B && tail[2] == 0x01 && tail[3] == 0x02)) return false;

        int e = (int)(eocdPos - cdOff); // EOCD index inside tail
        long dataPad = paddingBytes;
        byte[] newTail = tail;

        if (z64RecPos >= 0)
        {
            int recIdx = (int)(z64RecPos - cdOff);
            WriteU64(tail, recIdx + 48, ReadU64(tail, recIdx + 48) + (ulong)paddingBytes);   // cd offset (64-bit)
            WriteU64(tail, e - 20 + 8, (ulong)(z64RecPos + paddingBytes));                    // locator -> moved z64 record
            if (cdOff32 != uint.MaxValue)
            {
                long no = cdOff + paddingBytes;
                WriteU32(tail, e + 16, no >= uint.MaxValue ? uint.MaxValue : (uint)no);
            }
        }
        else if (cdOff + paddingBytes >= uint.MaxValue)
        {
            // New offset doesn't fit in 32 bits: add a ZIP64 EOCD record + locator (76 bytes, counted in padding).
            if (paddingBytes < 76) return false;
            dataPad = paddingBytes - 76;
            long newCdOff = cdOff + dataPad;
            var z = new byte[76];
            WriteU32(z, 0, 0x06064B50);
            WriteU64(z, 4, 44);
            WriteU16(z, 12, 45); WriteU16(z, 14, 45);
            WriteU32(z, 16, 0); WriteU32(z, 20, 0);
            WriteU64(z, 24, ReadU16(tail, e + 8));
            WriteU64(z, 32, ReadU16(tail, e + 10));
            WriteU64(z, 40, (ulong)cdSize);
            WriteU64(z, 48, (ulong)newCdOff);
            WriteU32(z, 56, 0x07064B50);
            WriteU32(z, 60, 0);
            WriteU64(z, 64, (ulong)(eocdPos + dataPad)); // where the z64 record will sit (old EOCD position, shifted)
            WriteU32(z, 72, 1);
            newTail = new byte[tail.Length + 76];
            Array.Copy(tail, 0, newTail, 0, e);
            Array.Copy(z, 0, newTail, e, 76);
            Array.Copy(tail, e, newTail, e + 76, tail.Length - e);
            WriteU32(newTail, e + 76 + 16, uint.MaxValue);
        }
        else
        {
            WriteU32(tail, e + 16, (uint)(cdOff + paddingBytes));
        }

        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, BufSize);
        fs.SetLength(cdOff);
        fs.Seek(0, SeekOrigin.End);
        WritePaddingRaw(fs, dataPad, progress, ct);
        fs.Write(newTail, 0, newTail.Length);
        fs.Flush();
        progress.Report((paddingBytes, ""));
        return true;
    }

    // ════════════════════════════════════════════════
    //  Generic fallback: append zeros after file
    //  Works for WAV, AU, BMP, GIF, FLAC, ISO, EXE, etc.
    // ════════════════════════════════════════════════

    static void InflateGeneric(string source, string output, long paddingBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        if (!SamePath(source, output))
            File.Copy(source, output, overwrite: true);
        AppendZeros(output, paddingBytes, progress, ct);
    }

    static void AppendZeros(string output, long paddingBytes, IProgress<(long, string)> progress, CancellationToken ct)
    {
        using var fs = new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.None, BufSize);
        fs.Seek(0, SeekOrigin.End);
        WritePaddingRaw(fs, paddingBytes, progress, ct);
    }

    // ════════════════════════════════════════════════
    //  Raw padding writer
    // ════════════════════════════════════════════════

    static void WritePaddingRaw(Stream stream, long totalBytes,
        IProgress<(long, string)> progress, CancellationToken ct)
    {
        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize]; // zero-filled
        long written = 0;
        while (written < totalBytes)
        {
            ct.ThrowIfCancellationRequested();
            int toWrite = (int)Math.Min(chunkSize, totalBytes - written);
            stream.Write(buffer, 0, toWrite);
            written += toWrite;
            if (written % (chunkSize * 4) == 0 || written >= totalBytes)
                progress.Report((written, ""));
        }
        stream.Flush();
    }

    // ════════════════════════════════════════════════
    //  Binary helpers
    // ════════════════════════════════════════════════

    static void WriteBE32(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    static void WriteBE64(Stream s, long value)
    {
        WriteBE32(s, (int)(value >> 32));
        WriteBE32(s, (int)value);
    }

    static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    static ulong ReadU64(byte[] b, int o) => ReadU32(b, o) | ((ulong)ReadU32(b, o + 4) << 32);
    static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void WriteU32(byte[] b, int o, uint v) { WriteU16(b, o, (ushort)v); WriteU16(b, o + 2, (ushort)(v >> 16)); }
    static void WriteU64(byte[] b, int o, ulong v) { WriteU32(b, o, (uint)v); WriteU32(b, o + 4, (uint)(v >> 32)); }

    // ════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════

    public static bool SamePath(string a, string b)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), cmp);
    }

    /// <summary>True if <paramref name="path"/> is the same as, or located inside, <paramref name="folder"/>.</summary>
    public static bool IsInsideOrSame(string folder, string path)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(f, p, cmp)) return true;
        return p.StartsWith(f + Path.DirectorySeparatorChar, cmp) || p.StartsWith(f + Path.AltDirectorySeparatorChar, cmp);
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Parses "1.5", "1,5" etc. (current culture first, then invariant) and scales by unit (0=KB, 1=MB, 2=GB).</summary>
    public static long ParseTargetBytes(string? text, int unitIndex)
    {
        var t = (text ?? "").Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out double val) &&
            !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out val) &&
            !double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            val = 1;
        if (double.IsNaN(val) || val < 0) val = 0;
        double mult = unitIndex switch { 0 => 1024.0, 1 => 1024.0 * 1024, 2 => 1024.0 * 1024 * 1024, _ => 1024.0 * 1024 };
        double bytes = val * mult;
        if (bytes > 4e18) bytes = 4e18; // clamp: avoid long overflow (disk check will report it)
        return (long)bytes;
    }

    /// <summary>Returns an error message if there is not enough free space for <paramref name="requiredBytes"/>, else null.</summary>
    public static string? CheckDiskSpace(string outputPath, long requiredBytes)
    {
        try
        {
            var full = Path.GetFullPath(outputPath);
            string probe;
            if (OperatingSystem.IsWindows())
            {
                probe = Path.GetPathRoot(full) ?? "";
                if (string.IsNullOrEmpty(probe)) return null;
            }
            else
            {
                // Unix: statvfs on the nearest existing directory (the filesystem the output will land on)
                probe = Path.GetDirectoryName(full) ?? "/";
                while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } up) probe = up;
            }
            var drive = new DriveInfo(probe);
            if (OperatingSystem.IsWindows() && !drive.IsReady) return null;
            long free = drive.AvailableFreeSpace;
            if (free >= requiredBytes) return null;

            return
                $"Not enough disk space on {drive.Name}\n\n" +
                $"Required:   {FormatSize(requiredBytes)}\n" +
                $"Available:  {FormatSize(free)}\n" +
                $"Short by:   {FormatSize(requiredBytes - free)}\n\n" +
                $"Free up space or choose a different output location.";
        }
        catch { return null; } // UNC paths, odd mounts: can't tell — don't block the user
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

// ══════════════════════════════════════════════════
//  PNG CRC-32 (standard polynomial 0xEDB88320)
// ══════════════════════════════════════════════════

internal static class Crc32Png
{
    static readonly uint[] Table;

    static Crc32Png()
    {
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            Table[i] = c;
        }
    }

    public static uint Init() => 0xFFFFFFFF;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Final(uint crc) => crc ^ 0xFFFFFFFF;
}
