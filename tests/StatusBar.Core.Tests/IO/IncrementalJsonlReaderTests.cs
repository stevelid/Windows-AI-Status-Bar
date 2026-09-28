using System.Text;
using StatusBar.Core.IO;

namespace StatusBar.Core.Tests.IO;

public class IncrementalJsonlReaderTests
{
    [Fact]
    public void Holds_an_incomplete_line_until_the_newline_arrives()
    {
        using var file = new TempJsonlFile();
        var reader = new IncrementalJsonlReader(file.Path);
        file.Write("{\"first\":1}");

        var first = reader.ReadNewLines();

        Assert.Empty(first.Lines);
        Assert.Equal(Encoding.UTF8.GetByteCount("{\"first\":1}"), first.Offset);
        file.Append("\n{\"second\":2}\n");

        var second = reader.ReadNewLines();

        Assert.Equal(["{\"first\":1}", "{\"second\":2}"], second.Lines);
        Assert.Equal(file.Length, second.Offset);
    }

    [Fact]
    public void Removes_carriage_returns_from_crlf_lines()
    {
        using var file = new TempJsonlFile();
        file.Write("one\r\ntwo\r\n");
        var reader = new IncrementalJsonlReader(file.Path);

        var result = reader.ReadNewLines();

        Assert.Equal(["one", "two"], result.Lines);
    }

    [Fact]
    public void Preserves_utf8_when_a_multibyte_character_spans_reads_and_buffer_chunks()
    {
        using var file = new TempJsonlFile();
        var reader = new IncrementalJsonlReader(file.Path, bufferSize: 1);
        var completeLine = "{\"text\":\"café\"}";
        var bytes = Encoding.UTF8.GetBytes(completeLine + "\n");
        var splitAfterLeadingByte = Array.IndexOf(bytes, (byte)0xC3) + 1;
        file.Write(bytes[..splitAfterLeadingByte]);

        Assert.Empty(reader.ReadNewLines().Lines);
        file.Append(bytes[splitAfterLeadingByte..]);

        var result = reader.ReadNewLines();

        Assert.Equal([completeLine], result.Lines);
    }

    [Fact]
    public void Resets_and_reads_from_the_start_after_truncation()
    {
        using var file = new TempJsonlFile();
        file.Write("a much longer line\n");
        var reader = new IncrementalJsonlReader(file.Path);
        Assert.False(reader.ReadNewLines().Reset);

        file.Write("new\n");
        var result = reader.ReadNewLines();

        Assert.True(result.Reset);
        Assert.Equal(["new"], result.Lines);
        Assert.Equal(file.Length, result.Offset);
    }

    [Fact]
    public void Resets_when_the_file_creation_time_changes_even_if_it_is_longer()
    {
        using var file = new TempJsonlFile();
        var fileId = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reader = new IncrementalJsonlReader(file.Path, fileIdProvider: _ => fileId);
        file.Write("old\n");
        Assert.False(reader.ReadNewLines().Reset);

        file.Write("replacement\n");
        fileId = fileId.AddMinutes(1);
        var result = reader.ReadNewLines();

        Assert.True(result.Reset);
        Assert.Equal(["replacement"], result.Lines);
    }

    [Fact]
    public void Handles_deletion_and_recreation_between_reads()
    {
        using var file = new TempJsonlFile();
        file.Write("before\n");
        var reader = new IncrementalJsonlReader(file.Path);
        Assert.Equal(["before"], reader.ReadNewLines().Lines);
        file.Delete();

        var missing = reader.ReadNewLines();

        Assert.True(missing.Reset);
        Assert.Empty(missing.Lines);
        Assert.Equal(0, missing.Offset);

        file.Write("after\n");
        var recreated = reader.ReadNewLines();

        Assert.Equal(["after"], recreated.Lines);
    }

    [Fact]
    public void Empty_file_returns_no_lines_without_resetting()
    {
        using var file = new TempJsonlFile();
        file.Write("");
        var reader = new IncrementalJsonlReader(file.Path);

        var result = reader.ReadNewLines();

        Assert.Empty(result.Lines);
        Assert.False(result.Reset);
        Assert.Equal(0, result.Offset);
    }

    [Fact]
    public void Tail_reader_skips_the_first_partial_line_and_ignores_a_trailing_partial_line()
    {
        using var file = new TempJsonlFile();
        file.Write("one\npartial-prefix\ntwo\nunfinished");

        var result = TailReader.Read(file.Path, maxBytes: 15);

        Assert.Equal(["two"], result);
    }

    [Fact]
    public void Tail_reader_reads_only_a_bounded_suffix_of_a_five_megabyte_file()
    {
        using var file = new TempJsonlFile();
        file.Write(new string('x', 5 * 1024 * 1024) + "\nfirst\nsecond\n");

        var result = TailReader.Read(file.Path, maxBytes: 32);

        Assert.Equal(["first", "second"], result);
    }

    sealed class TempJsonlFile : IDisposable
    {
        readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "statusbar-jsonl-" + Guid.NewGuid().ToString("N"));

        internal TempJsonlFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "data.jsonl");
        }

        internal string Path { get; }
        internal long Length => new FileInfo(Path).Length;

        internal void Write(string text) => File.WriteAllText(Path, text);
        internal void Write(byte[] bytes) => File.WriteAllBytes(Path, bytes);

        internal void Append(string text)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes);
        }

        internal void Append(byte[] bytes)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes);
        }

        internal void Delete() => File.Delete(Path);

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }
}
