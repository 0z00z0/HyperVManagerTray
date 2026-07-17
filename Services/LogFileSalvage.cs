namespace HyperVManagerTray.Services;

/// <summary>
/// Repairs a log file whose head has been replaced by NUL bytes (issue #55).
///
/// <para><b>How the damage happens.</b> The writer appends with a flush per line, which pushes the
/// bytes into the OS file cache — not to the platter. NTFS journals the file's new <em>length</em>
/// eagerly but the data blocks are written lazily, so a power cut or bugcheck can leave the file at
/// its full size with the freshly-appended range never committed. Reads of an allocated-but-unwritten
/// range return zeros, which is why the file opens as a wall of NULs. On Espen's machine
/// <c>switcher.log</c> begins with ~705 KB of them.</para>
///
/// <para><b>NLog does not fix this.</b> NLog's <c>FileTarget</c> with <c>AutoFlush</c> flushes to the
/// same OS cache; it does not open with write-through or call <c>FlushFileBuffers</c>. The failure mode
/// therefore survives the migration, and rotation only bounds how much of a log one such event can
/// eat. Recovering an already-damaged file is code we have to write — hence this class.</para>
///
/// <para><b>What we do about it.</b> Salvage rather than discard: the bytes after the NUL run are
/// intact log content and are exactly what a diagnosis needs. We shift the surviving tail to the front
/// of the file and truncate, which turns a 705 KB-of-NULs file into a usable log with its real history
/// kept. Deleting or archiving it would bound the size while throwing that history away.</para>
///
/// <para>Every failure path is a no-op: this runs at startup, before the log exists to complain to,
/// and a log file we cannot repair is never a reason to interfere with the app starting.</para>
/// </summary>
internal static class LogFileSalvage
{
    /// <summary>
    /// How far in we are willing to look for the first real byte. Comfortably past the ~705 KB seen in
    /// the wild; beyond this the file is treated as unsalvageable garbage and truncated rather than
    /// scanned indefinitely.
    /// </summary>
    private const long MaxScanBytes = 32L * 1024 * 1024;

    /// <summary>
    /// How far past the first surviving byte we will look for a line break. The first byte to survive
    /// almost certainly sits mid-line, so we start the repaired file at the next clean line boundary;
    /// if none turns up within this window we keep the partial line rather than discard good content.
    /// </summary>
    private const int MaxLineScanBytes = 64 * 1024;

    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Strips a leading run of NUL bytes from <paramref name="path"/>, keeping the surviving tail.
    /// </summary>
    /// <returns>
    /// True when the file was NUL-headed and has been repaired; false when it was healthy, absent,
    /// empty, or could not be opened (e.g. another instance or Notepad holds it).
    /// </returns>
    public static bool TrySalvage(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            // FileShare.None: repairing a file another process is appending to would race its writes.
            // Failing to take it exclusively is the normal, expected outcome in that case — skip.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (fs.Length == 0) return false;

            // Cheapest possible exit: healthy files fail on their first byte and pay nothing more.
            if (fs.ReadByte() != 0) return false;

            long firstGood = FindFirstNonNul(fs);

            // Nothing but NULs (within our scan budget): there is no history to keep.
            if (firstGood < 0)
            {
                fs.SetLength(0);
                return true;
            }

            firstGood = AdvanceToLineStart(fs, firstGood);
            ShiftTailToFront(fs, firstGood);
            return true;
        }
        catch (IOException)                 { return false; }  // locked / device error
        catch (UnauthorizedAccessException) { return false; }  // read-only / denied
        catch (NotSupportedException)       { return false; }  // exotic path
    }

    /// <summary>Offset of the first non-NUL byte, or -1 if there is none within the scan budget.</summary>
    private static long FindFirstNonNul(FileStream fs)
    {
        var buffer = new byte[CopyBufferSize];
        long offset = 0;
        long limit  = Math.Min(fs.Length, MaxScanBytes);

        fs.Position = 0;
        while (offset < limit)
        {
            int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - offset));
            if (read <= 0) break;

            int index = buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0);
            if (index >= 0) return offset + index;

            offset += read;
        }
        return -1;
    }

    /// <summary>
    /// Moves <paramref name="firstGood"/> forward to just after the next line break, so the repaired
    /// file starts on a whole line. Returns <paramref name="firstGood"/> unchanged if no break turns up
    /// within <see cref="MaxLineScanBytes"/> — a partial first line beats dropping real content.
    /// </summary>
    private static long AdvanceToLineStart(FileStream fs, long firstGood)
    {
        long limit = Math.Min(fs.Length, firstGood + MaxLineScanBytes);
        var buffer = new byte[CopyBufferSize];
        long offset = firstGood;

        fs.Position = firstGood;
        while (offset < limit)
        {
            int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, limit - offset));
            if (read <= 0) break;

            int index = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (index >= 0)
            {
                long lineStart = offset + index + 1;
                // A newline as the very last byte would leave an empty file; the partial line is better.
                return lineStart < fs.Length ? lineStart : firstGood;
            }
            offset += read;
        }
        return firstGood;
    }

    /// <summary>
    /// Copies [<paramref name="from"/>, EOF) to offset 0 and truncates. Safe in place: the destination
    /// always trails the source, so a forward copy never overwrites bytes it has yet to read.
    /// </summary>
    private static void ShiftTailToFront(FileStream fs, long from)
    {
        var buffer = new byte[CopyBufferSize];
        long src = from, dst = 0;

        while (src < fs.Length)
        {
            fs.Position = src;
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;

            fs.Position = dst;
            fs.Write(buffer, 0, read);

            src += read;
            dst += read;
        }

        fs.SetLength(dst);
        fs.Flush();
    }
}
