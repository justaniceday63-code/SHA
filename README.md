# SHA visualization

Ứng dụng Windows desktop cho việc kiểm tra tính toàn vẹn của file và xem
SHA-1 / SHA-256 thực sự hoạt động như thế nào.

Hàm nén chạy bằng **C** (`shaNative.dll`); giao diện là **WinUI 3**. Việc
duyệt thư mục, manifest, lịch sử và cài đặt được quản lý bằng mã managed.
Hai thuật toán được đặt cạnh nhau ở những chỗ quan trọng — 80 vòng so với 64,
quy tắc lịch tin nhắn khác nhau — và trang Learn đưa một khối dữ liệu đi qua
toàn bộ bảy giai đoạn của phép tính.

> **Lưu ý phạm vi:** SHA-1 tồn tại ở đây cho mục đích giáo dục và kiểm tra
> tương thích cũ. Nó không chống va chạm. Hãy dùng SHA-256 khi digest phải
> được tin tưởng.

## Tính năng

| Trang | Chức năng |
| --- | --- |
| **Home** | Trạng thái backend native (DLL đã nạp, khớp phiên bản ABI) và lối tắt. |
| **Verify** | Băm văn bản, một file hoặc cả thư mục; so sánh với digest kỳ vọng (khớp / không khớp); hủy một lần chạy dài; xem manifest thư mục. |
| **Learn** | Đi qua từng khối: từ từ khóa đầu vào → đệm → chia khối → lịch tin nhắn → nén → cập nhật trạng thái → digest, kèm trạng thái thanh ghi từng vòng. |
| **Collision demo** | Hai PDF SHAttered: byte khác nhau, SHA-1 giống nhau `38762cf7…`, SHA-256 khác nhau. Ứng dụng chỉ băm các file đóng sẵn — nó không thể và sẽ không tự tạo ra va chạm. |
| **History** | Mọi lần chạy được ghi vào SQLite cục bộ kèm thời gian và thông lượng, cùng biểu đồ thông lượng vẽ tay. |
| **Settings** | Thuật toán mặc định, cỡ bộ đệm, chính sách file ẩn / reparse, bật tắt lịch sử — lưu vào cùng cơ sở dữ liệu. |

## Cấu trúc kho

```
native/            backend C: sha1.c, sha256.c, streaming, file hashing, trace
  include/         shagui_native.h — ABI đóng băng (SHAGUI_ABI_VERSION 1)
  src/             hiện thực + sha_internal.h
  tests/           bài test native (vectors, streaming, file, trace)
src/
  ShaGui.Core/     lõi managed: P/Invoke, băm, manifest thư mục,
                   lịch sử SQLite, cài đặt
  ShaGui.App/      WinUI 3 shell và sáu trang
docs/              NATIVE_API.md, MANIFEST.md
```

## Yêu cầu

- Windows 10 1809+ (x64) — dự án nhắm `net8.0-windows10.0.19041.0`,
  OS tối thiểu `10.0.17763.0`
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Một trình biên dịch C cho DLL native: **MSVC** (Visual Studio Build Tools,
  bộ công cụ chính thức) hoặc **GCC** từ MSYS2 (ý kiến thứ hai)
- [WinApp CLI](https://learn.microsoft.com/windows/apps/) `winapp` để chạy
  và kiểm thử UI (`winget install Microsoft.WinApp`)

## Build

Build gồm hai bước — bước native trước, vì `ShaGui.Core` sẽ báo lỗi ngay và
rõ ràng nếu `shaNative.dll` thiếu:

```powershell
# Bước 1 — backend native (shaNative.dll).
# Mở "x64 Native Tools Command Prompt for VS 2022" rồi chạy từ thư mục gốc:
New-Item -ItemType Directory -Force native\build\Release\msvc | Out-Null
Push-Location native\build\Release\msvc
cl /nologo /W4 /std:c17 /utf-8 /D_CRT_SECURE_NO_WARNINGS /O2 /DSHAGUI_BUILD /LD `
   /I..\..\..\include /I..\..\..\src `
   ..\..\..\src\sha1.c ..\..\..\src\sha256.c ..\..\..\src\sha_api.c `
   ..\..\..\src\sha_ctx.c ..\..\..\src\sha_file.c ..\..\..\src\sha_trace.c `
   /Fe:shaNative.dll /link /IMPLIB:shaNative.lib
Pop-Location

# Bước 2 — solution managed (Core + App):
dotnet build ShaGui.sln -c Debug
```

DLL phải nằm chính xác tại `native\build\Release\msvc\shaNative.dll` — đó là
nơi `ShaGui.Core.csproj` tìm nó; nếu thiếu, build sẽ dừng với thông báo lỗi
hướng dẫn.

`Directory.Build.props` ghim mọi project C# về **x64**: DLL là x64, và một
test host AnyCPU có thể chạy thành tiến trình 32-bit và lỗi với
`BadImageFormatException` khó hiểu.

Muốn chạy bài test native, biên dịch tương tự nhưng thay `/LD` bằng
`/DSHAGUI_STATIC` và dùng file `native\tests\*.c` tương ứng.

## Chạy

```powershell
winapp run . --project ShaGui.App
```

Chế độ chạy đóng gói cần Developer Mode (hoặc certificate được tin tưởng).

## Đóng gói (MSIX)

```powershell
# Một lần: certificate phát triển khớp Publisher trong Package.appxmanifest
winapp cert generate --manifest .\src\ShaGui.App\Package.appxmanifest

# MSIX Release x64, ký bằng certificate đó
winapp package .\src\ShaGui.App\ShaGui.App.csproj --arch x64 --cert .\devcert.pfx
```

`devcert.pfx` chỉ dùng cho phát triển (mật khẩu mặc định `password`) và nằm
trong `.gitignore` — không bao giờ được phát hành. Bản thân ứng dụng chỉ là
x64 vì DLL native là x64. Cài file `.msix` tải từ GitHub Releases cần bật
Developer Mode hoặc tin certificate đã ký.

## Tài liệu

- [ARCHITECTURE.md](ARCHITECTURE.md) — các lớp, luồng dữ liệu, đa luồng, đóng gói
- [docs/NATIVE_API.md](docs/NATIVE_API.md) — ranh giới `shaNative.dll`:
  18 export, struct, mã trạng thái, cạm bẫy interop
- [docs/MANIFEST.md](docs/MANIFEST.md) — định dạng văn bản manifest thư mục
  (chuẩn hóa: mã hóa, thứ tự, chính sách duyệt)

## Ghi công

- File demo va chạm SHAttered: Marc Stevens (CWI Amsterdam), Elie Bursztein
  (Google), Pierre Karpman, Ange Albertini, Yarik Markov, Alex Petit Bianco,
  Clement Baisse — từ [shattered.io](https://shattered.io), đóng gói cho mục
  đích giáo dục.
- Tham chiếu SHA dẫn xuất từ các nguồn C gốc của SHA1 / SHA-256; được kiểm
  chứng bằng Python `hashlib` và .NET cryptography.
