using System.Runtime.InteropServices;
using camera_archiver.Native;

namespace camera_archiver;

internal record RawDevice(
    int Index,
    IntPtr ArrayBase,
    string? Vendor,
    string? Product,
    ushort VendorId,
    ushort ProductId,
    uint BusLocation,
    byte DeviceNumber)
{
    /// <summary>Pointer into the native array — valid until the array is freed.</summary>
    internal IntPtr Pointer => IntPtr.Add(ArrayBase, Index * LibMtp.RawDeviceSize);

    public string DisplayName =>
        Product ?? $"Unknown Device ({VendorId:X4}:{ProductId:X4})";
}

internal record StorageInfo(
    uint Id,
    ulong MaxCapacity,
    ulong FreeSpaceInBytes,
    string? Description,
    string? VolumeId);

internal record FileEntry(
    uint ItemId,
    uint ParentId,
    uint StorageId,
    string Name,
    ulong Size,
    bool IsFolder,
    DateTime? ModifiedAt);

internal sealed class OpenDevice : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    internal OpenDevice(IntPtr handle)
    {
        _handle = handle;
        FriendlyName     = LibMtp.ReadOwnedString(LibMtp.LIBMTP_Get_Friendlyname(_handle));
        ManufacturerName = LibMtp.ReadOwnedString(LibMtp.LIBMTP_Get_Manufacturername(_handle));
        ModelName        = LibMtp.ReadOwnedString(LibMtp.LIBMTP_Get_Modelname(_handle));
        SerialNumber     = LibMtp.ReadOwnedString(LibMtp.LIBMTP_Get_Serialnumber(_handle));
    }

    public string? FriendlyName     { get; }
    public string? ManufacturerName { get; }
    public string? ModelName        { get; }
    public string? SerialNumber     { get; }
    public string  DisplayName      => FriendlyName ?? ModelName ?? ManufacturerName ?? "Unknown Device";

    public List<StorageInfo> GetStorages()
    {
        LibMtp.LIBMTP_Get_Storage(_handle, 0);

        var result  = new List<StorageInfo>();
        var storPtr = Marshal.ReadIntPtr(_handle, LibMtp.Dev_StoragePtr);

        while (storPtr != IntPtr.Zero)
        {
            var id          = (uint)  Marshal.ReadInt32( storPtr, LibMtp.Stor_Id);
            var maxCap      = (ulong) Marshal.ReadInt64( storPtr, LibMtp.Stor_MaxCapacity);
            var freeBytes   = (ulong) Marshal.ReadInt64( storPtr, LibMtp.Stor_FreeBytes);
            var descPtr     =         Marshal.ReadIntPtr(storPtr, LibMtp.Stor_Description);
            var volPtr      =         Marshal.ReadIntPtr(storPtr, LibMtp.Stor_VolumeId);

            result.Add(new StorageInfo(id, maxCap, freeBytes,
                LibMtp.ReadString(descPtr), LibMtp.ReadString(volPtr)));

            storPtr = Marshal.ReadIntPtr(storPtr, LibMtp.Stor_Next);
        }

        return result;
    }

    public List<FileEntry> GetFilesAndFolders(uint storageId, uint parentId = LibMtp.Root)
    {
        var result  = new List<FileEntry>();
        var current = LibMtp.LIBMTP_Get_Files_And_Folders(_handle, storageId, parentId);

        while (current != IntPtr.Zero)
        {
            var itemId  = (uint)  Marshal.ReadInt32( current, LibMtp.File_ItemId);
            var pId     = (uint)  Marshal.ReadInt32( current, LibMtp.File_ParentId);
            var storId  = (uint)  Marshal.ReadInt32( current, LibMtp.File_StorId);
            var namePtr =         Marshal.ReadIntPtr(current, LibMtp.File_Name);
            var size    = (ulong) Marshal.ReadInt64( current, LibMtp.File_Size);
            var unixTs  =         Marshal.ReadInt64( current, LibMtp.File_ModDate);
            var type    =         Marshal.ReadInt32( current, LibMtp.File_Type);
            var name    = LibMtp.ReadString(namePtr) ?? "(unnamed)";
            var modDate = unixTs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unixTs).LocalDateTime
                : (DateTime?)null;

            result.Add(new FileEntry(itemId, pId, storId, name, size,
                type == LibMtp.FileType_Folder, modDate));

            // Must read next BEFORE destroy frees the node
            var next = Marshal.ReadIntPtr(current, LibMtp.File_Next);
            LibMtp.LIBMTP_destroy_file_t(current);
            current = next;
        }

        return result;
    }

    /// <summary>
    /// Resolves a slash-separated path (e.g. "/DCIM/100CANON") on the given storage
    /// to the folder's item ID. Returns null if any segment is not found.
    /// </summary>
    public uint? ResolvePath(uint storageId, string path)
    {
        var segments = path.Trim('/').Split('/')
            .Where(s => s.Length > 0).ToArray();

        uint parentId = LibMtp.Root;
        foreach (var seg in segments)
        {
            var entries = GetFilesAndFolders(storageId, parentId);
            var folder  = entries.FirstOrDefault(e =>
                e.IsFolder && string.Equals(e.Name, seg, StringComparison.OrdinalIgnoreCase));
            if (folder is null) return null;
            parentId = folder.ItemId;
        }
        return parentId;
    }

    /// <summary>
    /// Recursively enumerates all files under a folder.
    /// Files whose <see cref="FileEntry.ModifiedAt"/> is null are always included.
    /// </summary>
    public List<FileEntry> GetFilesRecursive(uint storageId, uint folderId, DateTime? newerThan = null)
    {
        var result  = new List<FileEntry>();
        var entries = GetFilesAndFolders(storageId, folderId);

        foreach (var entry in entries)
        {
            if (entry.IsFolder)
                result.AddRange(GetFilesRecursive(storageId, entry.ItemId, newerThan));
            else if (newerThan is null || entry.ModifiedAt is null || entry.ModifiedAt > newerThan)
                result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Downloads a file by item ID to a local path. Returns 0 on success.
    /// <paramref name="onProgress"/> receives (bytesSent, totalBytes) during transfer.
    /// </summary>
    public int DownloadFile(uint fileId, string localPath, Action<ulong, ulong>? onProgress = null)
    {
        LibMtp.ProgressCallback? cb = null;
        if (onProgress is not null)
            cb = (sent, total, _) => { onProgress(sent, total); return 0; };

        int result = LibMtp.LIBMTP_Get_File_To_File(_handle, fileId, localPath, cb, IntPtr.Zero);
        GC.KeepAlive(cb);   // prevent collection while native call is in progress
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        LibMtp.LIBMTP_Clear_Errorstack(_handle);
        LibMtp.LIBMTP_Release_Device(_handle);
        _disposed = true;
    }
}

internal static class MtpClient
{
    public static void Initialize() => LibMtp.LIBMTP_Init();

    /// <summary>
    /// Detect connected MTP devices.
    /// Caller must call <c>Marshal.FreeHGlobal(arrayPtr)</c> after opening
    /// any desired device (the array pointer backs <see cref="RawDevice.Pointer"/>).
    /// </summary>
    public static (List<RawDevice> Devices, IntPtr ArrayPtr) DetectDevices()
    {
        var err = LibMtp.LIBMTP_Detect_Raw_Devices(out var ptr, out var count);

        if (err == MtpError.NoDeviceAttached || count == 0)
            return ([], IntPtr.Zero);

        if (err != MtpError.None)
            throw new InvalidOperationException($"MTP device detection failed: {err}");

        var devices = new List<RawDevice>(count);
        for (int i = 0; i < count; i++)
        {
            var item      = IntPtr.Add(ptr, i * LibMtp.RawDeviceSize);
            var vendorPtr = Marshal.ReadIntPtr(item, LibMtp.Raw_VendorPtr);
            var prodPtr   = Marshal.ReadIntPtr(item, LibMtp.Raw_ProductPtr);
            var vendorId  = (ushort) Marshal.ReadInt16(item, LibMtp.Raw_VendorId);
            var productId = (ushort) Marshal.ReadInt16(item, LibMtp.Raw_ProductId);
            var bus       = (uint)   Marshal.ReadInt32(item, LibMtp.Raw_BusLocation);
            var devNum    =          Marshal.ReadByte( item, LibMtp.Raw_DevNum);

            devices.Add(new RawDevice(i, ptr,
                LibMtp.ReadString(vendorPtr), LibMtp.ReadString(prodPtr),
                vendorId, productId, bus, devNum));
        }

        return (devices, ptr);
    }

    /// <summary>Opens a raw device. Throws on failure.</summary>
    public static OpenDevice OpenDevice(RawDevice raw)
    {
        var handle = LibMtp.LIBMTP_Open_Raw_Device_Uncached(raw.Pointer);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to open device: {raw.DisplayName}");
        return new OpenDevice(handle);
    }
}
