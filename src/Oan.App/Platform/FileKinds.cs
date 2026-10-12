using Oan.Core.Model;

namespace Oan.App.Platform;

public enum FileKind { All, Folders, Documents, Images, Audio, Video, Archives, Code }

/// <summary>The category the toolbar filters by. Extension-based, so a guess: nothing else may depend on it.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    /// <summary>A folder's extension counts as empty whatever its name ends in, so only Folders can claim it.</summary>
    public static bool Matches(FileEntry entry, FileKind kind) => kind switch
    {
        FileKind.All => true,
        FileKind.Folders => entry.IsDirectory,
        _ => Of(entry.Extension) == kind,
    };

    /// <summary>Null for an extension in no category — not a kind of its own, just unclassified.</summary>
    public static FileKind? Of(string extension) =>
        ByExtension.TryGetValue(extension, out var kind) ? kind : null;

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);

        Add(FileKind.Documents, ".txt .md .rtf .pdf .doc .docx .odt .xls .xlsx .ods .csv .ppt .pptx .odp .epub .log");
        Add(FileKind.Images, ".jpg .jpeg .png .gif .bmp .webp .tif .tiff .svg .ico .heic .avif .psd .raw .cr2 .nef");
        Add(FileKind.Audio, ".mp3 .wav .flac .aac .ogg .oga .opus .m4a .wma .aiff .mid");
        Add(FileKind.Video, ".mp4 .mkv .avi .mov .wmv .webm .m4v .flv .mpg .mpeg .m2ts .ts");
        Add(FileKind.Archives, ".zip .rar .7z .tar .gz .tgz .bz2 .xz .zst .iso .cab .jar");
        Add(FileKind.Code, ".cs .fs .vb .c .h .cpp .hpp .rs .go .java .kt .swift .py .rb .php .pl .lua .js .mjs .ts .tsx .jsx .html .htm .css .scss .json .xml .yaml .yml .toml .ini .sql .sh .ps1 .psm1 .bat .cmd .gradle .cmake");

        return map;

        void Add(FileKind kind, string extensions)
        {
            foreach (var extension in extensions.Split(' ')) map[extension] = kind;
        }
    }
}
