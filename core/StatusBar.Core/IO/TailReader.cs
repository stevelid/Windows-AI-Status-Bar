namespace StatusBar.Core.IO;

internal static class TailReader
{
    const int DefaultBufferSize = 64 * 1024;

    internal static IReadOnlyList<string> Read(string path, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxBytes == 0) return [];

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
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        using (stream)
        {
            var fileLength = stream.Length;
            var byteCount = (int)Math.Min(fileLength, maxBytes);
            if (byteCount == 0) return [];

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
            if (start > 0)
            {
                var firstNewline = Array.IndexOf(bytes, (byte)'\n');
                if (firstNewline < 0) return [];
                return JsonlLineCodec.SplitCompleteLines(bytes, firstNewline + 1).Lines;
            }

            return JsonlLineCodec.SplitCompleteLines(bytes).Lines;
        }
    }
}
