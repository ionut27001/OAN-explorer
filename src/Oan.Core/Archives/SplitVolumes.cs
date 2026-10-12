namespace Oan.Core.Archives;

/// <summary>name.001, name.002, … read as the one file they were cut from, so the contents show in one step.</summary>
internal sealed class SplitVolumes : Stream
{
    private readonly List<(FileStream File, long Start)> _parts = [];
    private readonly long _length;
    private long _position;

    public SplitVolumes(string first)
    {
        var stem = first[..^3];
        for (var number = 1; File.Exists($"{stem}{number:000}"); number++)
        {
            var part = File.OpenRead($"{stem}{number:000}");
            _parts.Add((part, _length));
            _length += part.Length;
        }
    }

    public static bool IsFirst(string path) => path.EndsWith(".001", StringComparison.Ordinal);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length) return 0;

        var (file, start) = _parts.Last(p => p.Start <= _position);
        file.Position = _position - start;
        var read = file.Read(buffer, offset, count);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => _length + offset,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _parts.ForEach(p => p.File.Dispose());
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
