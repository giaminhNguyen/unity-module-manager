# Module Manager

Tool Editor cài / cập nhật / gỡ / phát hành từng phần của một framework Unity theo version, giống Package Manager thu nhỏ.
Danh sách module và nơi phát hành đều là dữ liệu, tool không gắn cứng vào framework nào.

- **Manager**: `Tools/Module Manager/Manager`. Dùng ở mọi game project.
- **Publisher**: `Tools/Module Manager/Publisher`. Chỉ dùng ở project nguồn (Dev mode).

Assembly riêng `ModuleManager.Editor`, `references: []`, chỉ chạy trong Editor, `autoReferenced: false`
(namespace `ModuleManager` không lọt vào code game). Code game lỗi compile thì tool vẫn mở được để sửa.

## Cài tool

Package Manager → **+** → **Add package from git URL**:

```
https://github.com/giaminhNguyen/unity-module-manager.git
```

Ghim version bằng tag: `https://github.com/giaminhNguyen/unity-module-manager.git#v1.0.0`.

---

## Khái niệm

| | |
|---|---|
| **Module** | Một nhóm thư mục/file cài và gỡ cùng nhau, khai bằng một file `<id>.module.json`. |
| **Manifest** | File `*.module.json`. Đi kèm module khi cài nên Manager đọc nó để biết module nào đang có. |
| **Nguồn** | Nơi chứa bản phát hành. `GitHub` (release của repo) hoặc `Folder` (thư mục trên máy/ổ mạng). |
| **Bản phát hành** | Một zip chứa file của module (kèm `.meta`) cùng manifest có `version` và `contentHash`. |

### Manifest

```json
{
    "id": "myframework.iap",
    "displayName": "IAP",
    "category": "Gameplay",
    "description": "Mua hàng, offer, restore...",
    "version": "1.0.0",
    "paths": [ "../Gameplay/IAP" ],
    "dependencies": [ { "id": "myframework.economy", "version": "1.0.0" } ],
    "packages": [ { "name": "com.cysharp.unitask", "source": "https://github.com/...git?path=..." } ],
    "contentHash": ""
}
```

- `paths` tính **tương đối thư mục chứa manifest** và phải nằm trong `Assets/`. Module cài ra đúng chỗ nó nằm ở project nguồn.
- `dependencies.version` là version **tối thiểu**. Manager tự cài kèm hoặc nâng module phụ thuộc.
- `packages`: package UPM cần có. Thiếu thì Manager thêm vào `Packages/manifest.json` sau khi cài.
- `version`, `contentHash` do Publisher ghi. Không sửa tay `contentHash`.

### Nguồn

Khai ở Manager → **Cấu hình** → **Thêm nguồn**, lưu trong `ProjectSettings/ModuleManagerSettings.json`.
Chưa có nguồn nào thì Manager hiện hướng dẫn thêm.

| Kiểu | `location` |
|---|---|
| `GitHub` | `owner/repo` |
| `Folder` | Đường dẫn thư mục (tuyệt đối, hoặc tương đối root project). Dùng thử offline hoặc chia sẻ qua ổ mạng. |

`installRoot` (ô **Thư mục cài**, tuỳ chọn): nơi đặt module trong project này, vd `Assets/Working`.
Manager thay thư mục cấp đầu dưới `Assets/` của gói bằng thư mục này: module phát hành từ `Assets/InWorking/Toppic/Base`
được cài vào `Assets/Working/Toppic/Base`. Để trống thì cài đúng đường dẫn như ở project nguồn.
Muốn dùng thì ở project nguồn, mọi module phải nằm chung dưới một thư mục cấp đầu trong `Assets/`.
Đổi `installRoot` khi đã có module cài: module nào được cập nhật sẽ bị xoá ở chỗ cũ rồi chép vào chỗ mới.

### Định dạng release trên GitHub

| | |
|---|---|
| Tag | `<id>/<version>` (vd `myframework.core/1.2.0`) |
| File | `<id>-<version>.zip` |
| Body | Changelog, cuối body là manifest dạng base64 trong comment HTML (GitHub không hiện) |

Manager chỉ cần liệt kê release là dựng được danh mục, không có file catalog riêng phải giữ đồng bộ.
Xoá một release trên GitHub là version đó biến mất khỏi Manager.

---

## Dùng ở game project

1. `Tools/Module Manager/Manager` → Cấu hình → thêm nguồn → nhập token (repo private) → **Fetch**.
2. Chọn version ở từng dòng → **Cài / Cập nhật / Hạ cấp / Cài lại**. Module phụ thuộc được cài kèm.
   **Cập nhật tất cả** nâng mọi module đang có lên bản mới nhất.
3. **Gỡ** bị chặn nếu module khác còn cần nó.

Cập nhật sẽ **xoá thư mục cũ của module rồi chép bản mới**. Không sửa tay file của module trong game project.
Muốn sửa thì sửa ở project nguồn rồi publish.

Thứ tự khi cài: tải hết gói → kiểm tra hết (đúng id/version, không ghi ra ngoài paths, không đè module khác) →
mới xoá/chép → `AssetDatabase.Refresh` một lần. Gói nào lỗi thì dừng trước khi đụng tới file.

## Phát hành (project nguồn)

Project nguồn là project chứa code gốc của các module.

1. Module Manager → **Cấu hình** → bật **Dev mode**. Manager khoá Cài/Gỡ ở project này để không ghi đè source.
2. `Tools/Module Manager/Publisher` → nhập token có quyền ghi → **Fetch**.
3. Cột trạng thái so `contentHash`: **Chưa publish** / **Khớp** / **Đã đổi**.
4. Chọn mức tăng (Patch/Minor/Major), ghi changelog, bấm **Publish** từng module, hoặc **Publish tất cả module đã đổi**
   (tự xếp module phụ thuộc lên trước).
5. Version mới được ghi vào manifest nguồn. Commit + push như bình thường.

Publisher chặn khi: manifest hỏng, trùng id, phụ thuộc không có hoặc vòng, path của hai module chồng nhau,
thiếu `.meta`, module phụ thuộc chưa publish đủ version.

## Tạo module và soát file chưa thuộc module

Mở Publisher → mục **Cấu hình dev** (lưu cùng `ProjectSettings/ModuleManagerSettings.json`):

| Field | Ý nghĩa |
|---|---|
| `manifestFolder` | Project path nơi tạo manifest mới (vd `Assets/Modules`). |
| `idPrefix` | Tiền tố id module mới (vd `myframework.`). |
| `auditRoot` | Project path để soát file chưa thuộc module. Trống = thư mục cha của `manifestFolder`. |

**Tạo module**

- Chọn thư mục/file trong cửa sổ Project → **Tạo module từ mục đang chọn**.
- Manifest mới ghi ở `<manifestFolder>/<id>.module.json`: `id` = `idPrefix` + tên mục đầu tiên (chữ thường, ký tự ngoài
  `[a-z0-9.-]` đổi thành `-`), `category` = tên thư mục cha, `version` `1.0.0`, `paths` tương đối từ `manifestFolder`.
  Đã có file cùng tên thì báo lỗi, không ghi đè.
- Sửa `description`, `dependencies`, `packages` bằng tay rồi mở Unity để sinh `.meta` → Publish.

**Chưa thuộc module nào (n)**

- Liệt kê dưới `auditRoot` các đường dẫn không nằm trong module nào. Thư mục là tổ tiên của path module thì đi vào trong
  và báo từng file lẻ; thư mục hoàn toàn không liên quan thì báo nguyên thư mục. Bỏ qua `.meta`, file manifest, file ẩn.
- Mỗi dòng có **Tạo module** (module mới cho riêng mục đó) hoặc chọn module rồi **Thêm vào** (thêm path vào manifest có sẵn;
  trùng hoặc chồng path đã có thì báo lỗi).
- Bấm **Quét lại** sau khi sửa file ngoài tool.

## Token GitHub

Fine-grained token, chỉ chọn repo chứa bản phát hành:

- Cài (Manager): `Contents: Read-only`.
- Publish: `Contents: Read and write`.

Token lưu theo máy trong EditorPrefs (`ModuleManager.Token.<owner/repo>`), không nằm trong project.
Repo public thì Manager không cần token.

## File tool đụng tới

| Đường dẫn | Vai trò |
|---|---|
| `ProjectSettings/ModuleManagerSettings.json` | Danh sách nguồn, Dev mode, cấu hình dev. Commit cùng project. Chưa có thì cấu hình trống. |
| `Library/ModuleManager/Downloads` | Zip đã tải |
| `Library/ModuleManager/Publish` | Zip vừa đóng gói |
| `Packages/manifest.json` | Thêm package UPM module cần |
| `<manifestFolder>/*.module.json` | Manifest tạo mới / thêm path (chỉ khi dùng tính năng soạn module) |

## Kiểm thử

Test EditMode ở `Tests/Editor`, chạy trên project giả ở thư mục tạm, không đụng Assets.
Package cài từ git/file không hiện test mặc định, thêm vào `Packages/manifest.json` của project:

```json
{
    "dependencies": { "com.modulemanager.editor": "https://github.com/giaminhNguyen/unity-module-manager.git" },
    "testables": [ "com.modulemanager.editor" ]
}
```

Rồi mở Window → General → Test Runner → EditMode.

`RealSourceTests` kiểm tra manifest của một project nguồn thật khi đặt biến môi trường `MM_SOURCE_ROOT`
(xem đầu file test để biết thêm `MM_AUDIT_DIR`, `MM_INSTALL_IDS`, `MM_INSTALL_INTO`, `MM_FEED_OUT`). Không đặt thì test tự bỏ qua.
