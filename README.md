# Quản lý hồ sơ khách hàng và lịch sử chăm sóc khách hàng (RavenDB)

> Đồ án môn **Cơ sở dữ liệu NoSQL**.
>
> **Nhóm thực hiện:** Nhóm 3
    1. 2001230885 - Nguyễn Huy Thanh
	2. 2001223984 - Trần Anh Quốc
	3. 2001230167 - Nguyễn Phạm Huỳnh Đạt


## 1. Giới thiệu đề tài

Đây là ứng dụng web giúp trung tâm dịch vụ quản lý hồ sơ khách hàng và quá trình chăm sóc khách hàng trên RavenDB — một hệ quản trị cơ sở dữ liệu hướng tài liệu (document database). Ứng dụng theo dõi khách hàng, lịch hẹn, danh mục dịch vụ, nhật ký thực hiện dịch vụ và phản hồi đánh giá.

Đề tài không chỉ làm các chức năng nghiệp vụ, mà còn minh họa cách làm việc với mô hình NoSQL: collection, quy ước document id, tài liệu nhúng, tham chiếu giữa các document, static index và truy vấn bằng Raven Query Language (RQL). Toàn bộ dữ liệu được lưu trên RavenDB; dự án không dùng Entity Framework, SQL Server, SQLite hay MongoDB.

## 2. Công nghệ

- **.NET 10** / C#, **ASP.NET Core MVC** với Razor Views
- **RavenDB 7.2** (máy chủ 7.2, thư viện `RavenDB.Client` 7.2.6) và RavenDB Studio khi chạy local
- Truy vấn bằng **LINQ** trên `IAsyncDocumentSession` và **RQL**
- Giao diện Bootstrap 5, Bootstrap Icons
- Kiểm thử tự động bằng **xUnit** 2.9.3

Kiến trúc gọn: Controllers/Views gọi Data Layer `IRavenDbContext` (một `DocumentStore` dùng chung suốt vòng đời ứng dụng, mỗi request mở một Async Session). Trước khi ghi lịch hẹn, lịch sử chăm sóc và phản hồi, `DocumentValidator` kiểm tra tài liệu được tham chiếu còn tồn tại và các ràng buộc nhất quán giữa các document.

## 3. Chức năng

Ứng dụng có năm phân hệ nghiệp vụ, cộng với trang chủ và trang chạy thử RQL.

**Trang chủ** hiển thị trạng thái kết nối RavenDB, số lượng document trong từng collection và một vài bản ghi gần nhất.

**Khách hàng (`Customers`).** Thêm, xem, sửa, xóa hồ sơ. Tìm theo họ tên, số điện thoại hoặc email (full-text `Search` của RavenDB). Lọc theo trạng thái. Giới tính được lưu `Male` / `Female` / `Other` và hiển thị Nam / Nữ / Khác; dữ liệu cũ ghi bằng tiếng Việt vẫn được nhận diện khi đọc hoặc sửa. Có trang xác nhận trước khi xóa.

**Lịch hẹn (`Appointments`).** Thêm, xem, sửa, hủy lịch. Lọc theo trạng thái (`Pending`, `Confirmed`, `Completed`, `Cancelled`). Tìm theo tên hoặc số điện thoại khách hàng. Trước khi lưu, hệ thống kiểm tra khách hàng và dịch vụ được chọn còn tồn tại. Hủy lịch hẹn là đổi trạng thái thành `Cancelled`, không xóa document.

**Dịch vụ (`Services`).** Thêm, xem, sửa, xóa danh mục gói dịch vụ. Tìm theo tên hoặc loại dịch vụ. Form có kiểm tra dữ liệu (tên, loại bắt buộc, giá không âm). Không cho xóa dịch vụ đang được lịch hẹn hoặc lịch sử chăm sóc sử dụng.

**Lịch sử chăm sóc (`ServiceHistories`).** Ghi nhận việc thực hiện dịch vụ thực tế: thêm, xem, sửa, xóa. Lọc theo khách hàng, nhân viên, dịch vụ và khoảng ngày; các bộ lọc này chạy trên static index `ServiceHistories/ByCustomerAndDate`. Khách hàng và dịch vụ được lấy từ lịch hẹn đã chọn. Trước khi lưu, hệ thống kiểm tra lịch hẹn, nhân viên còn tồn tại, và `CustomerId` / `ServiceId` phải khớp với lịch hẹn.

**Phản hồi (`Feedbacks`).** Thêm, xem, sửa, xóa đánh giá của khách hàng. Lọc theo khách hàng và điểm. Điểm từ 1 đến 5 sao. Phản hồi gắn trực tiếp với lịch hẹn; `CustomerId` phải là đúng người đặt lịch đó.

Ngoài ra:

- Form các phân hệ dịch vụ, lịch sử chăm sóc và phản hồi dùng Data Annotations; các thao tác ghi có chống giả mạo form (`ValidateAntiForgeryToken`).
- Khi mở chi tiết, sửa hoặc xóa, nếu id thuộc collection khác (ví dụ đưa id lịch hẹn vào trang khách hàng) thì trả về 404.
- Trang `/Home/RqlQueries` cung cấp các câu RQL mẫu để chạy thử ngay trong ứng dụng.
- Endpoint `/healthz` dùng cho kiểm tra sức khỏe khi triển khai.

Danh sách nhân viên (`Staffs`) không có trang quản lý riêng. Dữ liệu nhân viên được nạp sẵn cùng dữ liệu mẫu và được chọn khi lập lịch sử chăm sóc.

## 4. Mô hình dữ liệu NoSQL

Cơ sở dữ liệu **`CustomerCareDB`** gồm sáu collection. Địa chỉ khách hàng là **embedded document** nằm trong document khách hàng, không tách collection riêng. Các liên kết còn lại lưu dưới dạng chuỗi id tham chiếu (ví dụ `"customers/001"`).

| Collection | Quy ước id | Nội dung chính |
|---|---|---|
| `Customers` | `customers/{id}` | Họ tên, điện thoại, email, địa chỉ nhúng, ngày sinh, giới tính, trạng thái |
| `Staffs` | `staffs/{id}` | Nhân viên kỹ thuật: họ tên, chức vụ, điện thoại, email |
| `Services` | `services/{id}` | Tên, mô tả, giá, loại dịch vụ |
| `Appointments` | `appointments/{id}` | Tham chiếu `CustomerId`, `ServiceId`; ngày hẹn, trạng thái, ghi chú `Note` |
| `ServiceHistories` | `servicehistories/{id}` | Tham chiếu `AppointmentId`, `CustomerId`, `ServiceId`, `StaffId`; ngày thực hiện `ServiceDate`, mô tả, kết quả, ghi chú |
| `Feedbacks` | `feedbacks/{id}` | Tham chiếu `CustomerId`, `AppointmentId`; điểm `Rating` (1–5), nhận xét |

Dữ liệu mẫu khi khởi động gồm `customers/001`, `customers/002`, `staffs/001`, `staffs/002`, `services/001`, `services/002`, `appointments/001`, `appointments/002`, `servicehistories/001` và `feedbacks/001`. Khách hàng tạo mới để RavenDB tự cấp id (dạng `customers/1-A`).

**Ràng buộc toàn vẹn** (kiểm tra trước khi ghi):

- Lịch hẹn: `CustomerId` và `ServiceId` phải trỏ tới document đang tồn tại đúng collection.
- Lịch sử chăm sóc: lịch hẹn và nhân viên phải tồn tại; `CustomerId` / `ServiceId` phải trùng với lịch hẹn tương ứng.
- Phản hồi: lịch hẹn phải tồn tại; `CustomerId` phải trùng người đặt lịch; `Rating` trong khoảng 1–5.

**Static index `ServiceHistories/ByCustomerAndDate`** được định nghĩa bằng C# (`AbstractIndexCreationTask`) và tự triển khai khi ứng dụng khởi động. Index map các trường `CustomerId`, `ServiceDate`, `StaffId`, `ServiceId` để trang lịch sử chăm sóc lọc và sắp xếp trên index này, không quét cả collection. Các truy vấn khác (ví dụ tìm khách theo số điện thoại) để RavenDB tự tạo auto-index khi cần.

Ví dụ RQL:

```rql
from Customers where Phone = '0901234567'
```

```rql
from Appointments as a
load a.CustomerId as c
select {
    AppointmentId: id(a),
    CustomerName: c.FullName,
    CustomerPhone: c.Phone,
    AppointmentDate: a.AppointmentDate,
    Status: a.Status
}
```

```rql
from index 'ServiceHistories/ByCustomerAndDate'
where CustomerId = 'customers/001'
order by ServiceDate desc
```

Cú pháp `load` cho phép nạp document được tham chiếu trong cùng một truy vấn, không cần join kiểu quan hệ. Có thể chạy các câu này trong RavenDB Studio (`Indexes` → `Query`) hoặc trên trang `/Home/RqlQueries`. Danh sách đầy đủ các câu mẫu nằm ở [`Docs/RQL_QUERIES.md`](Docs/RQL_QUERIES.md).

## 5. Cấu trúc thư mục

```text
CustomerCareRavenDB/                       # Thư mục gốc repository
├── CustomerCareRavenDB.sln                # Solution dùng để build và test
├── CustomerCareRavenDB/                   # Ứng dụng web ASP.NET Core MVC
│   ├── Controllers/                       # Home, Customers, Appointments,
│   │                                      # Services, ServiceHistories, Feedbacks
│   ├── Data/                              # RavenDbContext, Seeder, Validator,
│   │                                      # ServiceHistoryQueries, SessionExtensions
│   ├── Indexes/                           # Static index ServiceHistories/ByCustomerAndDate
│   ├── Models/                            # Document models và CustomerGender
│   ├── ViewModels/                        # Form dịch vụ, lịch sử, phản hồi
│   ├── Views/
│   ├── appsettings.json
│   └── Program.cs                         # DI, tạo database, index, dữ liệu mẫu, /healthz
├── CustomerCareRavenDB.Tests/             # Kiểm thử xUnit
├── Docs/                                  # Thiết kế CSDL, RQL, hướng dẫn Studio
├── docker/entrypoint.sh                   # Khởi động RavenDB rồi tới ứng dụng
├── Dockerfile
├── render.yaml
├── .github/workflows/ci.yml
├── .env.example
└── README.md
```

Thiết kế chi tiết các collection nằm ở [`Docs/DATABASE_DESIGN.md`](Docs/DATABASE_DESIGN.md). Cách xem document và index trên Studio nằm ở [`Docs/RAVENDB_STUDIO_GUIDE.md`](Docs/RAVENDB_STUDIO_GUIDE.md).

## 6. Chạy local

Cần **.NET 10 SDK**, **Git**, và một máy chủ RavenDB 7.2 trên cổng 8080. RavenDB có thể chạy bằng Docker hoặc bản cài Windows.

### 6.1. Clone mã nguồn

```bash
git clone https://github.com/slyvie2702/CustomerCareRavenDB.git
cd CustomerCareRavenDB
```

### 6.2. Chạy RavenDB bằng Docker

```bash
docker run -d --name ravendb -p 8080:8080 -p 38888:38888 \
  -e RAVEN_Setup_Mode=None \
  -e RAVEN_License_Eula_Accepted=true \
  -e RAVEN_Security_UnsecuredAccessAllowed=PublicNetwork \
  ravendb/ravendb:7.2-latest
```

Mở [http://127.0.0.1:8080](http://127.0.0.1:8080) thấy RavenDB Studio là được. Những lần sau chỉ cần `docker start ravendb`.

### 6.3. Chạy RavenDB trên Windows (không dùng Docker)

Tải bản zip cho Windows tại [trang tải RavenDB](https://ravendb.net/download), giải nén, chạy `run.ps1`. Khi trình hướng dẫn hiện ra, chọn chế độ **Unsecure** và giữ cổng 8080.

### 6.4. Chạy ứng dụng

Từ thư mục gốc của repository:

```bash
cd CustomerCareRavenDB
dotnet run --launch-profile http
```

Mở [http://localhost:5069](http://localhost:5069). Ở môi trường Development, ứng dụng tự tạo database `CustomerCareDB`, triển khai static index và nạp dữ liệu mẫu (seeder idempotent: đã có dữ liệu mẫu thì không ghi đè). Không cần thao tác thêm trên Studio trước khi dùng.

Muốn trở lại bộ dữ liệu sạch giữa các lần demo: trong Studio, xóa database `CustomerCareDB`, rồi chạy lại `dotnet run --launch-profile http`.

Ở môi trường khác Development, thêm tham số `--seed` hoặc đặt `Seed__OnStartup=true` nếu muốn nạp dữ liệu mẫu khi khởi động.

## 7. Chạy test

RavenDB phải đang chạy tại `http://127.0.0.1:8080` (đổi địa chỉ bằng biến môi trường `RAVEN_TEST_URL` nếu cần). Đứng ở thư mục gốc của repository:

```bash
dotnet test CustomerCareRavenDB.sln
```

Bộ kiểm thử hiện có 91 ca (model, kết nối, chứng chỉ, giới tính, toàn vẹn tham chiếu, id khác collection, seeder, static index). GitHub Actions cũng chạy bộ test này và một bước smoke test image Docker khi có push hoặc pull request vào nhánh `main`.

## 8. Cấu hình

Kết nối RavenDB nằm trong section `RavenSettings` của `CustomerCareRavenDB/appsettings.json`, có thể ghi đè bằng biến môi trường theo quy ước `Section__Key`. Mẫu đầy đủ ở [`.env.example`](.env.example).

| Khóa | Ý nghĩa |
|---|---|
| `RavenSettings:Urls` | Danh sách URL máy chủ RavenDB. Local mặc định `http://127.0.0.1:8080`. Biến môi trường: `RavenSettings__Urls__0`. |
| `RavenSettings:DatabaseName` | Tên database, mặc định `CustomerCareDB`. |
| `RavenSettings:CertificatePath` | Đường dẫn file chứng chỉ client `.pfx` khi RavenDB bật HTTPS. |
| `RavenSettings:CertificateBase64` | Nội dung chứng chỉ `.pfx` mã hóa Base64 (tiện khi chạy trên PaaS). Được ưu tiên hơn `CertificatePath`. |
| `RavenSettings:CertificatePassword` | Mật khẩu chứng chỉ, dùng chung cho cả hai cách trên. |
| `RavenSettings:EnsureDatabaseOnStartup` | Tự tạo database nếu chưa có (mặc định `true`). Thiếu quyền tạo nhưng database đã tồn tại thì ứng dụng vẫn chạy tiếp. |
| `RavenSettings:StudioEnabled` | Hiện liên kết RavenDB Studio trên giao diện (mặc định `true`). Đặt `false` khi RavenDB chỉ chạy nội bộ trong container. |
| `Seed:OnStartup` | Nạp dữ liệu mẫu khi khởi động. Development luôn nạp; Production trên Render đặt `true`. |
| `Diagnostics:EnableRqlEndpoint` | Bật endpoint chạy RQL trực tiếp (`/Diagnostic/ExecuteRql`). Mặc định `true` trong Development, `false` ở môi trường khác. |

Chạy trong container/PaaS còn dùng `PORT` (cổng lắng nghe), `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` khi đứng sau reverse proxy, và `EMBEDDED_RAVENDB` (xem phần triển khai).

Môi trường Development dùng kết nối HTTP không bảo mật, phù hợp máy cá nhân và buổi bảo vệ. Nếu kết nối tới RavenDB có HTTPS, cần cung cấp chứng chỉ client (tối thiểu quyền Admin trên database để triển khai index).

## 9. Triển khai

Ứng dụng được triển khai thành **một web service** trên Render (gói Free, region Singapore). Một container chạy đồng thời RavenDB (chỉ lắng nghe `127.0.0.1:8080` và `38888`, không mở ra Internet) và ứng dụng ASP.NET Core (lắng nghe `0.0.0.0:$PORT`). Lý do gộp chung: service Free không nhận được lưu lượng qua mạng riêng, và không có đĩa lưu trữ bền.

Bản chạy thử: [https://customercare-ravendb.onrender.com](https://customercare-ravendb.onrender.com)

Lưu ý trên gói Free:

- **Dữ liệu sẽ bị reset.** Không có đĩa bền, nên mọi thay đổi mất khi service khởi động lại, deploy lại hoặc bị đưa vào trạng thái ngủ. Mỗi lần khởi động, ứng dụng nạp lại bộ dữ liệu mẫu (`Seed__OnStartup=true`).
- Service **ngủ sau khoảng 15 phút không có truy cập**. Lần mở tiếp theo cần chờ khởi động lại (thường khoảng 1–2 phút). Nên mở sẵn site vài phút trước khi demo.
- RavenDB Studio **không mở được** từ Internet (`RavenSettings__StudioEnabled=false`). Muốn xem document JSON và chạy RQL trên Studio, hãy dùng bản chạy local.

Cấu hình nằm trong `Dockerfile`, `docker/entrypoint.sh` và `render.yaml`. Health check: `/healthz`. Đẩy code lên nhánh `main` thì Render tự deploy lại sau khi CI trên GitHub đạt.

Chạy image giống bản deploy (tùy chọn, trên máy có Docker):

```bash
docker build -t customercare-ravendb .
docker run --rm -p 10000:10000 --memory=512m customercare-ravendb
```

Mở [http://localhost:10000](http://localhost:10000).
