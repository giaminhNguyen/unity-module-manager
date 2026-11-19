# Changelog

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
