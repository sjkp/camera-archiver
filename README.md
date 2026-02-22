# camera-archiver

A .NET 10 console app for browsing and archiving files from MTP devices (cameras, phones) via [libmtp](https://github.com/libmtp/libmtp).

## Features

- **Browser mode** — interactively explore the device file system
- **Copy mode** — copy files newer than a given date to a local folder, with a live progress bar
- Recursive folder traversal
- Shows file sizes and modification dates
- No external NuGet packages — uses P/Invoke directly against the native `libmtp` library

## Prerequisites

### macOS

```bash
brew install libmtp dotnet
```

The app looks for `libmtp.dylib` at:
- `/opt/homebrew/lib/libmtp.dylib` (Apple Silicon)
- `/usr/local/lib/libmtp.dylib` (Intel)

### Linux

```bash
# Debian/Ubuntu
sudo apt install libmtp-dev dotnet-sdk-10.0

# Fedora
sudo dnf install libmtp dotnet-sdk-10.0
```

## Usage

### Browser mode

```
dotnet run
```

Connect to the first detected MTP device and navigate its file system interactively.

```
Camera Archiver - MTP Device Browser
=====================================

Scanning for MTP devices... 1 found.

Opening Canon EOS R5... Connected.

  Friendly Name : My Camera
  Manufacturer  : Canon Inc.
  Model         : Canon EOS R5
  Serial        : 1234567890

Storage (1 unit(s)):
  [0] Internal Storage               15.2 GB free /  32.0 GB total  (52% used)

Browsing: Internal Storage

Commands: <folder>  enter folder  |  ..  go up  |  ls  list  |  q  quit

/>
  DCIM/                                          <DIR>  2024-11-03 09:15
  MISC/                                          <DIR>  2024-10-01 12:00

/> DCIM
  100CANON/                                      <DIR>  2024-11-03 09:15

/DCIM> 100CANON
  IMG_0001.CR3                              25.3 MB  2024-11-03 09:15
  IMG_0002.CR3                              24.1 MB  2024-11-03 09:22

/DCIM/100CANON> q
```

**Commands:**

| Command    | Action                  |
|------------|-------------------------|
| `<folder>` | Enter a folder          |
| `..`       | Go up one level         |
| `ls`       | Re-list current folder  |
| `q`        | Quit                    |

### Copy mode

```
dotnet run -- --to <output-dir> [--from <device-path>] [--since <date>] [--device <n>]
```

| Flag        | Description                                              | Default |
|-------------|----------------------------------------------------------|---------|
| `--to`      | Local output directory (created if it does not exist)    | —       |
| `--from`    | Source folder on the device                              | `/`     |
| `--since`   | Only copy files newer than this date (`yyyy-MM-dd`)      | all files |
| `--device`  | Device index when multiple devices are connected         | `0`     |

#### Examples

```bash
# Copy everything from DCIM shot after November 2024
dotnet run -- --from /DCIM --since 2024-11-01 --to ~/Desktop/photos

# Copy everything from the device
dotnet run -- --to ./backup

# Copy from a specific subfolder, second connected device
dotnet run -- --from /DCIM/100CANON --to ./raw --device 1
```

#### Progress output

```
Collecting files... 23 file(s) newer than 2024-11-01.
Output : /Users/sjkp/Desktop/photos

  [  1/23] IMG_0001.CR3             [████████████░░░░░░░░]  60%  15.2 MB / 25.3 MB
  [  1/23] IMG_0001.CR3               25.3 MB  2024-11-03 09:15
  [  2/23] IMG_0002.CR3             [████████████████████] 100%  24.1 MB / 24.1 MB
  ...

Done: 23 copied, 0 skipped, 0 failed.  (571.8 MB total)
```

Files that already exist in the output directory are skipped. Partial files are deleted if a transfer fails.

## Building

```bash
dotnet build
dotnet run
```

To produce a self-contained binary:

```bash
dotnet publish -c Release -r osx-arm64 --self-contained
```

## Project structure

```
camera-archiver/
├── Native/
│   └── LibMtp.cs        P/Invoke declarations and struct layout constants
├── MtpBrowser.cs        Managed wrappers: RawDevice, OpenDevice, MtpClient
├── Program.cs           Entry point, browser and copy mode
└── camera-archiver.csproj
```

## How it works

libmtp is a C library that speaks the [Media Transfer Protocol](https://en.wikipedia.org/wiki/Media_Transfer_Protocol) over USB. This app calls it via P/Invoke using manually computed struct field offsets for the 64-bit C ABI. There is no NuGet wrapper — the native library must be installed separately.

Key native functions used:

| Function | Purpose |
|---|---|
| `LIBMTP_Detect_Raw_Devices` | Enumerate connected MTP devices |
| `LIBMTP_Open_Raw_Device_Uncached` | Open a device handle |
| `LIBMTP_Get_Storage` | Populate the device's storage list |
| `LIBMTP_Get_Files_And_Folders` | List a folder's contents |
| `LIBMTP_Get_File_To_File` | Download a file with progress callback |
| `LIBMTP_Release_Device` | Close and free a device handle |

## Connecting your device

- **Camera** — set the USB connection mode to **MTP** or **PTP** (not "Mass Storage")
- **Android phone** — pull down the notification shade after plugging in and choose **File Transfer / MTP**
- **macOS** — macOS does not include a native MTP driver; libmtp communicates directly over USB via libusb (included as a libmtp dependency)
