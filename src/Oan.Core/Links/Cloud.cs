namespace Oan.Core.Links;

/// <summary>Whether the bytes of a file are on this disk. An online-only file is still listed, sized and dated like any other.</summary>
public enum CloudState
{
    /// <summary>Not under a cloud provider, or the provider says nothing about this item.</summary>
    NotCloud,

    /// <summary>Contents are not here. Opening it downloads it.</summary>
    OnlineOnly,

    /// <summary>Contents are here, and the provider may reclaim the space later.</summary>
    OnThisDevice,

    /// <summary>Pinned: contents are here and are kept here.</summary>
    AlwaysAvailable,
}

/// <summary>The cloud state of an entry, read off attributes the listing already has: no syscall, no handle, nothing hydrated.</summary>
public static class Cloud
{
    private const uint FILE_ATTRIBUTE_PINNED = 0x0008_0000;
    private const uint FILE_ATTRIBUTE_UNPINNED = 0x0010_0000;
    private const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x0004_0000;
    private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x0040_0000;

    /// <summary>Order matters: dehydration is asked first, because a file can be both pinned and not yet downloaded.</summary>
    public static CloudState StateOf(FileAttributes attributes)
    {
        var raw = (uint)attributes;

        if ((raw & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN)) != 0)
            return CloudState.OnlineOnly;

        if ((raw & FILE_ATTRIBUTE_PINNED) != 0) return CloudState.AlwaysAvailable;
        if ((raw & FILE_ATTRIBUTE_UNPINNED) != 0) return CloudState.OnThisDevice;

        return CloudState.NotCloud;
    }

    /// <summary>The word for the Kind column, or null when there is nothing to say.</summary>
    public static string? Label(CloudState state) => state switch
    {
        CloudState.OnlineOnly => "Online only",
        CloudState.OnThisDevice => "On this device",
        CloudState.AlwaysAvailable => "Always available",
        _ => null,
    };

    /// <summary>True when reading this item's contents would pull them over the network. Ask before previewing, hashing or searching inside.</summary>
    public static bool WouldDownload(FileAttributes attributes) => StateOf(attributes) == CloudState.OnlineOnly;
}
