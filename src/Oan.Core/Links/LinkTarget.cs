namespace Oan.Core.Links;

/// <summary>The two shortcut formats Windows ships.</summary>
public enum LinkKind
{
    /// <summary>A .lnk: a binary shell link, read through IShellLink.</summary>
    ShellLink,

    /// <summary>A .url: an INI file with a URL in it.</summary>
    InternetShortcut,
}

/// <summary>Where a shortcut leads, as far as a file manager needs to decide what to do.</summary>
public enum LinkLeadsTo
{
    /// <summary>Nothing could be established, or the link points at a place with no file path.</summary>
    Unknown,

    Folder,
    File,

    /// <summary>A web address. Only a .url can be this.</summary>
    Url,
}

/// <summary>What a shortcut says; every field not established is null. Navigate to <see cref="FolderToEnter"/> when set; otherwise hand
/// <see cref="LinkPath"/>, not <see cref="Target"/>, to the shell: the shortcut carries arguments, a working directory and a window state.</summary>
public sealed record LinkTarget
{
    public required string LinkPath { get; init; }
    public required LinkKind Kind { get; init; }

    /// <summary>The path or URL the shortcut names. Null when it names neither.</summary>
    public string? Target { get; init; }

    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }

    /// <summary>Where the shortcut's own icon comes from, when it overrides the target's.</summary>
    public string? IconFile { get; init; }

    /// <summary>Null without <see cref="IconFile"/>.</summary>
    public int? IconIndex { get; init; }

    /// <summary>Null when not asked or not answered in time; always null for a URL.</summary>
    public bool? TargetExists { get; init; }

    /// <summary>Whether the target was a folder when the shortcut was written; read from the .lnk, so known even for a missing target.</summary>
    public bool? TargetWasDirectory { get; init; }

    public LinkLeadsTo LeadsTo { get; init; }

    /// <summary>One sentence for the status bar. Null when there is nothing to report.</summary>
    public string? Refusal { get; init; }

    /// <summary>The folder to navigate to inside OAN, or null if this shortcut is not one.</summary>
    public string? FolderToEnter => LeadsTo == LinkLeadsTo.Folder ? Target : null;

    /// <summary>True when the target was named and looked for, and is not there.</summary>
    public bool IsBroken => TargetExists == false;
}
