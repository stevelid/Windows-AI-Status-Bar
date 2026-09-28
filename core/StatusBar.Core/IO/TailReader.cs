namespace StatusBar.Core.IO;

internal static class TailReader
{
    const int DefaultBufferSize = 64 * 1024;

    internal static IReadOnlyList<string> Read(string path, int maxBytes)
        => ReadWithOffset(path, maxBytes).Lines;

    internal static TailReadResult ReadWithOffset(string path, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxBytes == 0) return new TailReadResult([], 0, 0);

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                DefaultBufferSize,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return new TailReadResult([], 0, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new TailReadResult([], 0, 0);
        }

        using (stream)
        {
            var fileLength = stream.Length;
            var byteCount = (int)Math.Min(fileLength, maxBytes);
            if (byteCount == 0) return new TailReadResult([], 0, 0);

            var start = fileLength - byteCount;
            stream.Position = start;
            var bytes = new byte[byteCount];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) break;
                offset += read;
            }

            if (offset != bytes.Length) Array.Resize(ref bytes, offset);
            var firstLineStart = 0;
            if (start > 0)
            {
                var firstNewline = Array.IndexOf(bytes, (byte)'\n');
                if (firstNewline < 0) return new TailReadResult([], 0, 0);
                firstLineStart = firstNewline + 1;
            }

            var split = JsonlLineCodec.SplitCompleteLines(bytes, firstLineStart);
            var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
            var nextOffset = lastNewline < firstLineStart
                ? start + firstLineStart
                : start + lastNewline + 1;
            return new TailReadResult(split.Lines, nextOffset, start + firstLineStart);
        }
    }
}

internal sealed record TailReadResult(IReadOnlyList<string> Lines, long Offset, long FirstLineOffset);
