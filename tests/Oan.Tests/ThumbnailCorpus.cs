using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oan.Tests;

/// <summary>
/// One folder holding one file of every kind the thumbnail measurements name, built rather than
/// checked in: a repository is the wrong place for a 900 MB video, and a JPEG the tests encode
/// themselves is a JPEG with known dimensions, which a downloaded one is not.
///
/// What cannot be built is pointed at instead. OAN_THUMB_MP4 and OAN_THUMB_PDF name real files
/// to use; without them those two rows are reported as missing, never as "no thumbnail" — a
/// measurement that silently drops its subject is worse than one that says it is short.
/// </summary>
sealed class ThumbnailCorpus : IDisposable
{
    private readonly TempFolder _temp = new();

    public string Root => _temp.Root;

    /// <summary>A 1600x1200 photograph-shaped JPEG: the case the whole engine exists for.</summary>
    public string Jpeg => _jpeg ??= Encode("photo.jpg", new JpegBitmapEncoder(), 1600, 1200, opaque: true);

    /// <summary>512x512 with a transparent corner, which is where the alpha handling shows up.</summary>
    public string Png => _png ??= Encode("image.png", new PngBitmapEncoder(), 512, 512, opaque: false);

    public string Folder => _folder ??= _temp.AddFolder("empty");

    /// <summary>A folder with pictures in it — Explorer draws those on the folder.</summary>
    public string PictureFolder => _pictures ??= WithPictures();

    public string NoExtension => _none ??= _temp.AddFile("NOTICE", "no extension, no registered anything");

    public string Docx => _docx ??= WriteDocx();

    public string Pdf => _pdf ??= Environment.GetEnvironmentVariable("OAN_THUMB_PDF") is { Length: > 0 } given
        ? given
        : WritePdf();

    /// <summary>Null when this machine has no video to measure, which is said out loud.</summary>
    public string? Mp4 => Environment.GetEnvironmentVariable("OAN_THUMB_MP4") is { Length: > 0 } given && File.Exists(given)
        ? given
        : null;

    /// <summary>Not copied: an executable's icon lives inside it, and a copy is the same bytes.</summary>
    public static string Exe => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    /// <summary>A real shortcut, copied out of the Start menu. Null when this machine has none.</summary>
    public string? Shortcut => _shortcut ??= CopyAShortcut();

    /// <summary>
    /// The other kind of shortcut, and the one the overlay tests use: an .url is a text file, so
    /// it can be written rather than found, and Windows gives it the same arrow badge as a .lnk.
    /// </summary>
    public string UrlShortcut => _url ??= _temp.AddFile("site.url",
        "[InternetShortcut]" + Environment.NewLine + "URL=https://example.com/" + Environment.NewLine);

    private int _fresh;
    private string? _jpeg, _png, _folder, _pictures, _none, _docx, _pdf, _shortcut, _url;

    /// <summary>
    /// A copy of <paramref name="path"/> under a name nothing has ever seen, for measuring a cold
    /// extraction more than once — Windows keys its thumbnail cache on the file, so the second
    /// measurement of the same file is a measurement of the cache. Big files are handed back as
    /// they are: copying a gigabyte to time a decode measures the copy.
    /// </summary>
    public string Fresh(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length >= 32L * 1024 * 1024) return path;

        var copy = Path.Combine(_temp.Root, $"fresh-{_fresh++}{Path.GetExtension(path)}");
        File.Copy(path, copy);
        return copy;
    }

    public void Dispose() => _temp.Dispose();

    private string Encode(string name, BitmapEncoder encoder, int width, int height, bool opaque)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                var clear = !opaque && x + y < width / 4;

                pixels[i + 0] = clear ? (byte)0 : (byte)(x * 255 / width);
                pixels[i + 1] = clear ? (byte)0 : (byte)(y * 255 / height);
                pixels[i + 2] = clear ? (byte)0 : (byte)((x ^ y) & 0xFF);
                pixels[i + 3] = clear ? (byte)0 : (byte)255;
            }

        // Bgra32 and not Pbgra32: a premultiplied pixel with zero alpha may not carry colour, and
        // WPF is entitled to do anything at all with one that does.
        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        encoder.Frames.Add(BitmapFrame.Create(frame));

        var path = Path.Combine(_temp.Root, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    private string WithPictures()
    {
        var folder = _temp.AddFolder("pictures");
        File.Copy(Jpeg, Path.Combine(folder, "one.jpg"));
        File.Copy(Png, Path.Combine(folder, "two.png"));
        return folder;
    }

    /// <summary>
    /// A .docx is a zip of XML parts. Word only stores a picture of page one when the document was
    /// saved with "Save Thumbnail" ticked, so this one — like most real ones — has none, and what
    /// it measures is exactly that: the cost of a provider deciding it has nothing to give.
    /// </summary>
    private string WriteDocx()
    {
        var path = Path.Combine(_temp.Root, "letter.docx");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);

        Write(zip, "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");

        Write(zip, "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");

        Write(zip, "word/document.xml",
            """<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>OAN</w:t></w:r></w:p></w:body></w:document>""");

        return path;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// A valid one-page PDF, cross-reference table and all. Valid on purpose: if the answer comes
    /// back "no picture", it has to be because nothing is registered to draw PDFs on this machine,
    /// and not because the measurement handed the provider a broken file.
    /// </summary>
    private string WritePdf()
    {
        string[] objects =
        [
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            "<</Length 46>>\nstream\nBT /F1 24 Tf 20 100 Td (OAN thumbnail) Tj ET\nendstream",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        ];

        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();

        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(body.Length);
            body.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = body.Length;
        body.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) body.Append(offset.ToString("D10")).Append(" 00000 n \n");
        body.Append("trailer\n<</Size ").Append(objects.Length + 1).Append("/Root 1 0 R>>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");

        var path = Path.Combine(_temp.Root, "page.pdf");
        File.WriteAllText(path, body.ToString(), Encoding.ASCII);
        return path;
    }

    private string? CopyAShortcut()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.StartMenu })
        {
            var root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;

            var found = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).FirstOrDefault();
            if (found is null) continue;

            var copy = Path.Combine(_temp.Root, "shortcut.lnk");
            File.Copy(found, copy, overwrite: true);
            return copy;
        }

        return null;
    }
}
