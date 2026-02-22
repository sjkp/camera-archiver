using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace camera_archiver.Native;

internal enum MtpError
{
    None = 0,
    GeneralError = 1,
    PtpLayer = 2,
    UsbLayer = 3,
    MemoryAllocation = 4,
    NoDeviceAttached = 5,
    StorageFull = 6,
    ConnectionFailed = 7,
    Cancelled = 8,
}

internal static class LibMtp
{
    private const string Lib = "mtp";

    [ModuleInitializer]
    internal static void Init()
    {
        NativeLibrary.SetDllImportResolver(typeof(LibMtp).Assembly, static (name, _, _) =>
        {
            if (name != Lib) return IntPtr.Zero;

            string[] paths = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ["/opt/homebrew/lib/libmtp.dylib", "/usr/local/lib/libmtp.dylib", "libmtp.dylib"]
                : ["/usr/lib/x86_64-linux-gnu/libmtp.so.9", "/usr/lib/libmtp.so.9", "libmtp.so.9", "libmtp.so"];

            foreach (var p in paths)
                if (NativeLibrary.TryLoad(p, out var h)) return h;

            return IntPtr.Zero;
        });
    }

    // ── P/Invoke declarations ──────────────────────────────────────────────

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void LIBMTP_Init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern MtpError LIBMTP_Detect_Raw_Devices(out IntPtr devices, out int count);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr LIBMTP_Open_Raw_Device_Uncached(IntPtr rawDevice);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int LIBMTP_Get_Storage(IntPtr device, int sortby);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr LIBMTP_Get_Files_And_Folders(IntPtr device, uint storageId, uint parentId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void LIBMTP_Release_Device(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void LIBMTP_destroy_file_t(IntPtr file);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr LIBMTP_Get_Friendlyname(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr LIBMTP_Get_Manufacturername(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr LIBMTP_Get_Modelname(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr LIBMTP_Get_Serialnumber(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void LIBMTP_Clear_Errorstack(IntPtr device);

    /// <summary>Progress callback: return 0 to continue, non-zero to cancel.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(ulong sent, ulong total, IntPtr data);

    /// <summary>Download a file from the device to a local path. Returns 0 on success.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int LIBMTP_Get_File_To_File(
        IntPtr device,
        uint id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ProgressCallback? callback,
        IntPtr data);

    // ── Struct layout constants (64-bit, standard C alignment) ────────────

    // sizeof(LIBMTP_raw_device_t) = 40
    internal const int RawDeviceSize = 40;

    // Offsets within LIBMTP_raw_device_t (device_entry is embedded at offset 0)
    internal const int Raw_VendorPtr    = 0;   // char* (device_entry.vendor)
    internal const int Raw_VendorId     = 8;   // uint16
    internal const int Raw_ProductPtr   = 16;  // char* (device_entry.product)
    internal const int Raw_ProductId    = 24;  // uint16
    internal const int Raw_BusLocation  = 32;  // uint32
    internal const int Raw_DevNum       = 36;  // uint8

    // Offsets within LIBMTP_mtpdevice_t
    // uint8 object_bitsize(0) + padding(7) + void* params(8) + void* usbinfo(16)
    internal const int Dev_StoragePtr   = 24;  // LIBMTP_devicestorage_t*

    // Offsets within LIBMTP_devicestorage_t
    internal const int Stor_Id          = 0;   // uint32
    internal const int Stor_MaxCapacity = 16;  // uint64
    internal const int Stor_FreeBytes   = 24;  // uint64
    internal const int Stor_Description = 40;  // char*
    internal const int Stor_VolumeId    = 48;  // char*
    internal const int Stor_Next        = 56;  // struct*

    // Offsets within LIBMTP_file_t
    // uint32 item_id(0) + uint32 parent_id(4) + uint32 storage_id(8) + padding(12)
    internal const int File_ItemId   = 0;   // uint32
    internal const int File_ParentId = 4;   // uint32
    internal const int File_StorId   = 8;   // uint32
    internal const int File_Name     = 16;  // char*
    internal const int File_Size     = 24;  // uint64
    internal const int File_ModDate  = 32;  // time_t (int64 — Unix seconds)
    internal const int File_Type     = 40;  // int32 (LIBMTP_filetype_t enum)
    internal const int File_Next     = 48;  // struct*

    // LIBMTP_FILETYPE_FOLDER = 0 (first enum value)
    internal const int FileType_Folder = 0;

    // parentId value meaning root
    internal const uint Root = 0xFFFF_FFFF;

    // ── String helpers ────────────────────────────────────────────────────

    /// <summary>Read and free a strdup'd C string returned by libmtp.</summary>
    internal static string? ReadOwnedString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Read a C string from a pointer we do not own.</summary>
    internal static string? ReadString(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
}
