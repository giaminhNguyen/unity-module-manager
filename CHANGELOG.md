# Changelog

## 1.6.0

- Menu gộp còn một mục `Window/Module Manager` (bỏ `Tools/Module Manager/Manager` và `/Publisher`). Cửa sổ nhớ tab đang mở.

## 1.5.0

- Gộp Manager và Publisher thành một cửa sổ có 2 tab, dùng chung cấu hình. Menu `Manager` / `Publisher` mở thẳng tab tương ứng.
- Tab Publisher chỉ quét và tính hash khi mở lần đầu.

## 1.4.0

- Chọn nhiều mục: ô chọn ở từng dòng và ở từng thư mục (chọn / bỏ chọn cả nhóm) trong Manager và Publisher.
- Manager: **Cài / Cập nhật đã chọn**, **Gỡ đã chọn**, **Bỏ chọn**. Cả nhóm dùng một hộp xác nhận, một lần Refresh, một lần thêm/gỡ package; package chỉ bị gỡ khi không module nào ngoài nhóm còn khai.
- Publisher: **Áp mức tăng** cho mọi mục đã chọn, **Publish đã chọn**.
- Hộp thoại xác nhận rút gọn khi danh sách dài.

## 1.3.0

- Plugin UPM: manifest `paths` rỗng chỉ có `packages`. Cài = thêm package, gỡ = gỡ package không module nào khác còn khai.
- Plugin .unitypackage: trường `unityPackage` (tên file trong thư mục .unitypackage của Publisher, ngoài Assets/) và `importedPaths`. Cài = chép ra Library/ModuleManager/Import rồi `AssetDatabase.ImportPackage` không hỏi; gỡ = xoá `importedPaths`.
- Publisher: Cấu hình dev có thêm `unityPackageFolder` (mặc định `UnityPackages/`).
- Upload release đọc thẳng từ file, timeout tải/upload 1 giờ cho gói lớn.
- Manifest ghi thêm `unityPackage`, `importedPaths` nên hash của module đã publish bằng bản cũ sẽ hiện "Đã đổi" một lần.

## 1.2.0

- `category` chia nhiều cấp bằng `/` (vd `Toppic/UI`): Manager và Publisher hiện danh sách thành cây thư mục gập/mở được.

## 1.1.0

- Nguồn có thêm `installRoot` (ô Thư mục cài): cài module vào thư mục tuỳ chọn, vd `Assets/Working`, thay cho thư mục cấp đầu dưới `Assets/` của gói.

## 1.0.0

- Tách Module Manager thành UPM package độc lập, không gắn với framework nào.
- Namespace `ModuleManager`, assembly `ModuleManager.Editor` (không auto-reference vào code game).
- Bỏ nguồn mặc định gắn cứng: chưa có cấu hình thì danh sách nguồn trống, thêm ở Cấu hình.
- Cấu hình dev: `manifestFolder`, `idPrefix`, `auditRoot`.
- Publisher: tạo module từ mục đang chọn, soát file chưa thuộc module, thêm path vào module có sẵn.
