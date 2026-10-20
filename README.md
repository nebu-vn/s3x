<img width="1659" height="965" alt="image" src="https://github.com/user-attachments/assets/a645c8ad-dac9-4929-9f22-5732395262ef" />


# S3 Explorer (WPF, .NET 8)

A Windows Explorer-style S3 metadata browser. Browse object listings (name, size, type, date modified, storage class) from a local cache; download files/folders to your machine and delete files/folders on S3.

> The app's UI is in Vietnamese; UI labels below are given in English with the original label in parentheses.

## Running

```
dotnet run -c Release
```
Requirements: Windows, .NET 8 SDK.

## Features

- Add / edit / delete multiple S3 profiles (AWS S3 or S3-compatible: MinIO, Wasabi, R2…); each profile = bucket + prefix.
- **Test connection** button ("Kiểm tra kết nối") in the profile dialog.
- First time a profile is opened: the whole prefix is scanned with `ListObjectsV2` (1000 objects/page) and stored in a SQLite cache.
- Subsequent opens: loads instantly from cache, no S3 calls. Click **Rescan from S3** ("Quét lại từ S3", F5) when you want to refresh.
- Explorer-style view: folders first, then files; columns Name (with Windows icon), Date modified, Type, Size, File count (for folders: recursive total), Storage class.
- Each folder has its total file count and size (including subfolders) precomputed.
- Sort by column, filter by name, Back / Up, Enter opens a folder, Backspace goes up a level.
- Double-click a file (or the ⬇ button) to download it to the Downloads folder; right-click → "Download to another folder…" ("Tải về thư mục khác…"). Folders are downloaded recursively, preserving structure. Downloads run 4 files in parallel and never overwrite existing files (a " (1)" suffix is added automatically).
- 🗑 button / Delete key / right-click → "Delete from S3…" ("Xóa khỏi S3…"): permanently deletes a file or an entire folder (with a confirmation dialog). The cache is updated immediately, no rescan needed.
- Right-click: copy name / key / `s3://` URI.

## Storage

| File | Contents |
|---|---|
| `%LocalAppData%\S3Explorer\profiles.json` | Profile list. Secret keys are encrypted with Windows DPAPI (CurrentUser). |
| `%LocalAppData%\S3Explorer\cache.db` | SQLite cache: `objects`, `folders` (precomputed totals), `scans` (scan timestamps). |

When rescanning, the new cache is written in a single transaction: if the scan is cancelled or fails midway, the old cache stays intact.
Changing a profile's bucket / prefix / endpoint clears the old cache and rescans; changing only the keys/name keeps the cache.

## Structure

```
Models/S3Profile.cs       S3 profile
Models/S3Entry.cs         a row in the listing
Services/S3Service.cs     client creation, paginated ListObjectsV2
Services/Scanner.cs       scan S3 → cache
Services/CacheStore.cs    SQLite, per-folder totals
Services/ProfileStore.cs  persists profiles.json
Services/ShellInfo.cs     type name + icon by file extension (SHGetFileInfo)
Services/Crypto.cs        DPAPI
MainWindow.xaml(.cs)      main window
ProfileDialog.xaml(.cs)   add/edit profile
```

## Minimum IAM permissions

View only: `s3:ListBucket`. Download: add `s3:GetObject`. Delete: add `s3:DeleteObject`.

```json
[
  { "Effect": "Allow", "Action": "s3:ListBucket", "Resource": "arn:aws:s3:::BUCKET_NAME" },
  { "Effect": "Allow", "Action": ["s3:GetObject", "s3:DeleteObject"], "Resource": "arn:aws:s3:::BUCKET_NAME/*" }
]
```
