# S3 Explorer (WPF, .NET 8)

Trình duyệt metadata S3 kiểu Windows Explorer. Duyệt danh sách object (tên, dung lượng, loại, ngày sửa đổi, storage class) từ cache; tải file/thư mục về máy và xóa file/thư mục trên S3.

## Chạy

```
dotnet run -c Release
```
Yêu cầu: Windows, .NET 8 SDK.

## Tính năng

- Thêm / sửa / xóa nhiều cấu hình S3 (AWS S3 hoặc S3-compatible: MinIO, Wasabi, R2…), mỗi cấu hình = bucket + prefix.
- Nút **Kiểm tra kết nối** trong hộp thoại cấu hình.
- Lần đầu mở một cấu hình: quét toàn bộ prefix bằng `ListObjectsV2` (1000 object/trang), lưu vào cache SQLite.
- Những lần sau: mở ngay từ cache, không gọi S3. Bấm **Quét lại từ S3** (F5) khi muốn cập nhật.
- Xem dạng Explorer: thư mục trước, file sau; cột Tên (kèm icon Windows), Ngày sửa đổi, Loại, Kích thước, Số file (với thư mục: tổng đệ quy), Storage class.
- Mỗi thư mục có tổng số file và dung lượng (gồm thư mục con) được tính sẵn.
- Sắp xếp theo cột, lọc theo tên, Back / Up, Enter mở thư mục, Backspace lên cấp.
- Double-click file (hoặc nút ⬇) để tải về thư mục Downloads; chuột phải → "Tải về thư mục khác…". Thư mục được tải đệ quy, giữ cấu trúc. Tải song song 4 file, không ghi đè file đã có (tự thêm " (1)").
- Nút 🗑 / phím Delete / chuột phải → "Xóa khỏi S3…": xóa vĩnh viễn file hoặc cả thư mục (có hộp xác nhận). Cache được cập nhật ngay, không cần quét lại.
- Chuột phải: sao chép tên / key / `s3://` URI.

## Lưu trữ

| File | Nội dung |
|---|---|
| `%LocalAppData%\S3Explorer\profiles.json` | Danh sách cấu hình. Secret key mã hóa bằng Windows DPAPI (CurrentUser). |
| `%LocalAppData%\S3Explorer\cache.db` | Cache SQLite: `objects`, `folders` (tổng tính sẵn), `scans` (thời điểm quét). |

Khi quét lại, cache mới được ghi trong một transaction: nếu hủy hoặc lỗi giữa chừng, cache cũ vẫn giữ nguyên.
Sửa bucket / prefix / endpoint của một cấu hình sẽ xóa cache cũ và quét lại; chỉ đổi key/tên thì giữ cache.

## Cấu trúc

```
Models/S3Profile.cs       cấu hình S3
Models/S3Entry.cs         một dòng trong danh sách
Services/S3Service.cs     tạo client, ListObjectsV2 phân trang
Services/Scanner.cs       quét S3 → cache
Services/CacheStore.cs    SQLite, tính tổng theo thư mục
Services/ProfileStore.cs  lưu profiles.json
Services/ShellInfo.cs     tên loại + icon theo đuôi file (SHGetFileInfo)
Services/Crypto.cs        DPAPI
MainWindow.xaml(.cs)      giao diện chính
ProfileDialog.xaml(.cs)   thêm/sửa cấu hình
```

## Quyền IAM tối thiểu

Chỉ xem: `s3:ListBucket`. Tải về: thêm `s3:GetObject`. Xóa: thêm `s3:DeleteObject`.

```json
[
  { "Effect": "Allow", "Action": "s3:ListBucket", "Resource": "arn:aws:s3:::TEN_BUCKET" },
  { "Effect": "Allow", "Action": ["s3:GetObject", "s3:DeleteObject"], "Resource": "arn:aws:s3:::TEN_BUCKET/*" }
]
```
