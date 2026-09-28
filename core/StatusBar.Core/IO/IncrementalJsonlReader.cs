using System.Text;

namespace StatusBar.Core.IO;

internal sealed class IncrementalJsonlReader
{
    const int DefaultBufferSize = 64 * 1024;

    readonly string _path;
    readonly int _bufferSize;
    readonly Func<string, DateTime> _fileIdProvider;
    DateTime _fileId;
    bool _hasFileId;
    byte[] _pendingPartialLine = [];

    internal IncrementalJsonlReader(
        string path,
        int bufferSize = DefaultBufferSize,
        Func<string, DateTime>? fileIdProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (bufferSize < 1) throw new ArgumentOutOfRangeException(nameof(bufferSize));

        _path = path;
        _bufferSize = bufferSize;
        _fileIdProvider = fileIdProvider ?? File.GetCreationTimeUtc;
    }

    internal long Offset { get; private set; }

    internal JsonlReadResult ReadNewLines()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                _bufferSize,
                FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return OnFileMissing();
        }
        catch (DirectoryNotFoundException)
        {
            return OnFileMissing();
        }

        using (stream)
        {
            var fileId = _fileIdProvider(_path);
            var length = stream.Length;
            var reset = _hasFileId && (_fileId != fileId || length < Offset);
            if (reset)
            {
                Offset = 0;
                _pendingPartialLine = [];
            }

            _fileId = fileId;
            _hasFileId = true;
            stream.Position = Offset;

            var bytesToRead = length - Offset;
            if (bytesToRead <= 0)
                return new JsonlReadResult([], reset, Offset);

            using var delta = new MemoryStream();
            var buffer = new byte[_bufferSize];
            while (bytesToRead > 0)
            {
                var requested = (int)Math.Min(buffer.Length, bytesToRead);
                var read = stream.Read(buffer, 0, requested);
                if (read == 0) break;
                delta.Write(buffer, 0, read);
                bytesToRead -= read;
            }

            var newBytes = delta.ToArray();
            Offset += newBytes.LongLength;
            var combined = new byte[_pendingPartialLine.Length + newBytes.Length];
            Buffer.BlockCopy(_pendingPartialLine, 0, combined, 0, _pendingPartialLine.Length);
            Buffer.BlockCopy(newBytes, 0, combined, _pendingPartialLine.Length, newBytes.Length);

            var split = JsonlLineCodec.SplitCompleteLines(combined);
            _pendingPartialLine = split.Pending;
            return new JsonlReadResult(split.Lines, reset, Offset);
        }
    }

    JsonlReadResult OnFileMissing()
    {
        var reset = _hasFileId;
        _hasFileId = false;
        _fileId = default;
        Offset = 0;
        _pendingPartialLine = [];
        return new JsonlReadResult([], reset, Offset);
    }
}

internal sealed record JsonlReadResult(IReadOnlyList<string> Lines, bool Reset, long Offset);

internal static class JsonlLineCodec
{
    internal static (IReadOnlyList<string> Lines, byte[] Pending) SplitCompleteLines(
        byte[] bytes,
        int firstLineStart = 0)
    {
        var lines = new List<string>();
        var lineStart = firstLineStart;
        for (var index = firstLineStart; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;

            var lineLength = index - lineStart;
            if (lineLength > 0 && bytes[index - 1] == (byte)'\r') lineLength--;
            lines.Add(Encoding.UTF8.GetString(bytes, lineStart, lineLength));
            lineStart = index + 1;
        }

        return (lines, bytes.AsSpan(lineStart).ToArray());
    }
}
