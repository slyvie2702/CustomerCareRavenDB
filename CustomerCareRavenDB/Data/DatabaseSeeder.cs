using CustomerCareRavenDB.Data.Validation;
using CustomerCareRavenDB.Models;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;

namespace CustomerCareRavenDB.Data;

public interface IDatabaseSeeder
{
    Task<bool> SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Số lượng document trong bộ dữ liệu mẫu (cố định, sinh bằng <c>Random(20261118)</c>).
/// </summary>
public static class SeedCatalog
{
    public const int RandomSeed = 20261118;
    public static readonly DateTime AsOfUtc = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

    public const int CustomerCount = 70;
    public const int StaffCount = 10;
    public const int ServiceCount = 10;
    public const int AppointmentCount = 220;
}

/// <summary>
/// Quản lý việc khởi tạo dữ liệu mẫu ban đầu (Seeding) cho CustomerCareDB.
/// Idempotent: nếu <c>customers/001</c> đã tồn tại thì bỏ qua, không ghi đè.
/// Bộ mẫu gốc (id 001–002 và lịch sử/phản hồi 001) được giữ nguyên để các câu RQL demo
/// và các test lọc theo <c>customers/001</c> vẫn ra đúng bản ghi kinh điển.
/// Phần còn lại được sinh xác định bằng <see cref="SeedCatalog.RandomSeed"/>.
/// </summary>
public class DatabaseSeeder : IDatabaseSeeder
{
    private readonly IRavenDbContext _context;
    private readonly IDocumentValidator _validator;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        IRavenDbContext context,
        IDocumentValidator validator,
        ILogger<DatabaseSeeder> logger)
    {
        _context = context;
        _validator = validator;
        _logger = logger;
    }

    public async Task<bool> SeedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.EnsureDatabaseExistsAsync(cancellationToken: cancellationToken);

            using var session = _context.OpenAsyncSession();
            var existingCustomer = await session.LoadAsync<Customer>("customers/001", cancellationToken);
            if (existingCustomer != null)
            {
                _logger.LogInformation("Dữ liệu mẫu 'customers/001' đã tồn tại. Bỏ qua seeding để bảo toàn dữ liệu hiện tại.");
                return false;
            }

            _logger.LogInformation("Bắt đầu nạp bộ dữ liệu mẫu ({Customers} khách, {Staff} nhân viên, {Services} dịch vụ, {Appointments} lịch hẹn)...",
                SeedCatalog.CustomerCount, SeedCatalog.StaffCount, SeedCatalog.ServiceCount, SeedCatalog.AppointmentCount);

            var data = BuildSeedData();

            await using (var bulk = _context.Store.BulkInsert(_context.DatabaseName, cancellationToken))
            {
                foreach (var customer in data.Customers)
                    await bulk.StoreAsync(customer, customer.Id);
                foreach (var staff in data.Staffs)
                    await bulk.StoreAsync(staff, staff.Id);
                foreach (var service in data.Services)
                    await bulk.StoreAsync(service, service.Id);
                foreach (var appointment in data.Appointments)
                    await bulk.StoreAsync(appointment, appointment.Id);
                foreach (var history in data.Histories)
                    await bulk.StoreAsync(history, history.Id);
                foreach (var feedback in data.Feedbacks)
                    await bulk.StoreAsync(feedback, feedback.Id);
            }

            _logger.LogInformation(
                "Hoàn tất nạp dữ liệu mẫu: {Customers} khách hàng, {Staffs} nhân viên, {Services} dịch vụ, {Appointments} lịch hẹn, {Histories} lịch sử, {Feedbacks} phản hồi.",
                data.Customers.Count, data.Staffs.Count, data.Services.Count,
                data.Appointments.Count, data.Histories.Count, data.Feedbacks.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi xảy ra trong quá trình Seeding dữ liệu: {Message}", ex.Message);
            throw;
        }
    }

    public static SeedBundle BuildSeedData()
    {
        var customers = BuildCustomers();
        var staffs = BuildStaffs();
        var services = BuildServices();
        var appointments = BuildCanonicalAppointments();
        var histories = new List<ServiceHistory> { CanonicalHistory() };
        var feedbacks = new List<Feedback> { CanonicalFeedback() };

        var rng = new Random(SeedCatalog.RandomSeed);
        var asOf = SeedCatalog.AsOfUtc;
        var rangeStart = asOf.AddMonths(-12);
        var rangeEnd = asOf.AddMonths(2);

        for (var i = 3; i <= SeedCatalog.AppointmentCount; i++)
        {
            var appointmentDate = NextAppointmentDate(rng, rangeStart, rangeEnd);
            var isFuture = appointmentDate > asOf;
            string status;
            if (isFuture)
                status = rng.Next(100) < 55 ? "Pending" : "Confirmed";
            else
            {
                var roll = rng.Next(100);
                status = roll < 48 ? "Completed" : roll < 68 ? "Confirmed" : roll < 84 ? "Pending" : "Cancelled";
            }

            // Không gán thêm lịch hẹn cho customers/001: các câu RQL demo và bộ lọc
            // theo khách này vẫn ra đúng appointments/001 và servicehistories/001.
            var customer = customers[rng.Next(1, SeedCatalog.CustomerCount)];
            var service = services[rng.Next(services.Count)];
            var createdAt = appointmentDate.AddDays(-rng.Next(1, 14)).AddHours(-rng.Next(0, 8));
            if (createdAt < rangeStart.AddDays(-20))
                createdAt = rangeStart.AddDays(-rng.Next(1, 10));

            var appointmentId = Id("appointments", i);
            appointments.Add(new Appointment
            {
                Id = appointmentId,
                CustomerId = customer.Id,
                ServiceId = service.Id,
                AppointmentDate = appointmentDate,
                Status = status,
                Note = Pick(rng, AppointmentNotes),
                CreatedAt = createdAt
            });

            if (status != "Completed")
                continue;

            var staff = staffs[rng.Next(staffs.Count)];
            var serviceDate = appointmentDate.AddMinutes(rng.Next(30, 150));
            var historyId = Id("servicehistories", histories.Count + 1);
            histories.Add(new ServiceHistory
            {
                Id = historyId,
                AppointmentId = appointmentId,
                CustomerId = customer.Id,
                ServiceId = service.Id,
                StaffId = staff.Id,
                ServiceDate = serviceDate,
                Description = Pick(rng, HistoryDescriptions),
                Result = Pick(rng, HistoryResults),
                Note = Pick(rng, HistoryNotes),
                CreatedAt = serviceDate.AddMinutes(rng.Next(5, 40))
            });

            // Khoảng 3/4 lịch đã hoàn thành có phản hồi, điểm số đa dạng.
            if (rng.Next(100) >= 75)
                continue;

            var rating = WeightedRating(rng);
            feedbacks.Add(new Feedback
            {
                Id = Id("feedbacks", feedbacks.Count + 1),
                CustomerId = customer.Id,
                AppointmentId = appointmentId,
                Rating = rating,
                Comment = FeedbackComment(rng, rating),
                CreatedAt = serviceDate.AddHours(rng.Next(2, 72))
            });
        }

        return new SeedBundle(customers, staffs, services, appointments, histories, feedbacks);
    }

    private static List<Customer> BuildCustomers()
    {
        var list = new List<Customer>
        {
            new()
            {
                Id = "customers/001",
                FullName = "Nguyễn Văn An",
                Phone = "0901234567",
                Email = "nguyenvanan@example.com",
                Address = new CustomerAddress
                {
                    Street = "123 Nguyễn Huệ",
                    Ward = "Bến Nghé",
                    District = "Quận 1",
                    City = "TP. Hồ Chí Minh"
                },
                Birthday = new DateTime(1990, 5, 15, 0, 0, 0, DateTimeKind.Utc),
                Gender = CustomerGender.Male,
                CreatedAt = new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc),
                Status = "Active"
            },
            new()
            {
                Id = "customers/002",
                FullName = "Lê Thị Bích Ngọc",
                Phone = "0987654321",
                Email = "bichngoc.le@example.com",
                Address = new CustomerAddress
                {
                    Street = "45 Lê Lợi",
                    Ward = "Phường 4",
                    District = "Quận 3",
                    City = "TP. Hồ Chí Minh"
                },
                Birthday = new DateTime(1995, 11, 20, 0, 0, 0, DateTimeKind.Utc),
                Gender = CustomerGender.Female,
                CreatedAt = new DateTime(2026, 1, 15, 9, 30, 0, DateTimeKind.Utc),
                Status = "Active"
            }
        };

        var rng = new Random(SeedCatalog.RandomSeed);
        var usedPhones = new HashSet<string> { "0901234567", "0987654321" };
        var usedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nguyenvanan@example.com", "bichngoc.le@example.com"
        };

        var family = new[]
        {
            "Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Huỳnh", "Phan", "Vũ", "Võ",
            "Đặng", "Bùi", "Đỗ", "Hồ", "Ngô", "Dương", "Lý", "Đinh", "Trương"
        };
        var maleMiddle = new[] { "Văn", "Hữu", "Đức", "Minh", "Công", "Quang", "Thành", "Anh" };
        var femaleMiddle = new[] { "Thị", "Ngọc", "Thu", "Kim", "Minh", "Thanh", "Bích", "Khánh" };
        var otherMiddle = new[] { "Hoàng", "Minh", "Anh", "Lam", "Khánh" };
        var maleGiven = new[]
        {
            "Bình", "Dũng", "Hùng", "Khoa", "Long", "Nam", "Phong", "Quân", "Sơn", "Tài",
            "Thắng", "Trí", "Tuấn", "Việt", "Hải", "Khôi", "Lâm", "Phúc"
        };
        var femaleGiven = new[]
        {
            "Anh", "Chi", "Dung", "Hà", "Hương", "Lan", "Linh", "Mai", "My", "Nga",
            "Trang", "Vy", "Yến", "Thảo", "Quỳnh", "Ngân", "Tú", "Hạnh"
        };
        var otherGiven = new[] { "An", "Bình", "Khánh", "Linh", "Minh", "Ngọc", "Tâm", "Uyên" };

        for (var i = 3; i <= SeedCatalog.CustomerCount; i++)
        {
            var genderRoll = rng.Next(100);
            var gender = genderRoll < 47 ? CustomerGender.Male : genderRoll < 94 ? CustomerGender.Female : CustomerGender.Other;
            var (middle, given) = gender switch
            {
                CustomerGender.Female => (femaleMiddle, femaleGiven),
                CustomerGender.Other => (otherMiddle, otherGiven),
                _ => (maleMiddle, maleGiven)
            };
            var fullName = $"{Pick(rng, family)} {Pick(rng, middle)} {Pick(rng, given)}";
            var city = Cities[rng.Next(Cities.Length)];

            string phone;
            do
            {
                phone = $"{Pick(rng, new[] { "090", "091", "093", "094", "096", "097", "098", "032", "033", "034" })}{rng.Next(1000000, 9999999)}";
            }
            while (!usedPhones.Add(phone));

            var local = $"khach{i:D3}.{Slug(fullName)}";
            var email = $"{local}@example.com";
            var suffix = 2;
            while (!usedEmails.Add(email))
            {
                email = $"{local}{suffix}@example.com";
                suffix++;
            }

            var createdAt = SeedCatalog.AsOfUtc.AddDays(-rng.Next(20, 400)).AddHours(rng.Next(8, 18));
            list.Add(new Customer
            {
                Id = Id("customers", i),
                FullName = fullName,
                Phone = phone,
                Email = email,
                Address = new CustomerAddress
                {
                    Street = $"{rng.Next(1, 350)} {Pick(rng, Streets)}",
                    Ward = Pick(rng, city.Wards),
                    District = Pick(rng, city.Districts),
                    City = city.Name
                },
                Birthday = new DateTime(rng.Next(1975, 2005), rng.Next(1, 13), rng.Next(1, 28), 0, 0, 0, DateTimeKind.Utc),
                Gender = gender,
                CreatedAt = createdAt,
                Status = rng.Next(100) < 8 ? "Inactive" : "Active"
            });
        }

        return list;
    }

    private static List<Staff> BuildStaffs() =>
    [
        new()
        {
            Id = "staffs/001",
            FullName = "Trần Thị Mai",
            Position = "Chuyên viên kỹ thuật cao cấp",
            Phone = "0912345678",
            Email = "tranmai@centercare.vn",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)
        },
        new()
        {
            Id = "staffs/002",
            FullName = "Phạm Quốc Tuấn",
            Position = "Kỹ thuật viên hệ thống",
            Phone = "0933456789",
            Email = "tuantq@centercare.vn",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)
        },
        new() { Id = "staffs/003", FullName = "Nguyễn Hoàng Nam", Position = "Kỹ thuật viên phần cứng", Phone = "0908111222", Email = "namnh@centercare.vn", CreatedAt = new DateTime(2025, 8, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/004", FullName = "Lê Thị Thu Hà", Position = "Chuyên viên chăm sóc khách hàng", Phone = "0908222333", Email = "halt@centercare.vn", CreatedAt = new DateTime(2025, 8, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/005", FullName = "Đỗ Minh Khoa", Position = "Kỹ thuật viên phần mềm", Phone = "0908333444", Email = "khoadm@centercare.vn", CreatedAt = new DateTime(2025, 9, 15, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/006", FullName = "Võ Thanh Trúc", Position = "Kỹ thuật viên bảo dưỡng", Phone = "0908444555", Email = "trucvt@centercare.vn", CreatedAt = new DateTime(2025, 9, 15, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/007", FullName = "Phan Đức Hải", Position = "Kỹ thuật viên sửa chữa", Phone = "0908555666", Email = "haipd@centercare.vn", CreatedAt = new DateTime(2025, 10, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/008", FullName = "Bùi Ngọc Lan", Position = "Quản lý ca", Phone = "0908666777", Email = "lanbn@centercare.vn", CreatedAt = new DateTime(2025, 10, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/009", FullName = "Hoàng Anh Tuấn", Position = "Kỹ thuật viên mạng", Phone = "0908777888", Email = "tuanha@centercare.vn", CreatedAt = new DateTime(2025, 11, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "staffs/010", FullName = "Ngô Thị Phương", Position = "Tư vấn viên kỹ thuật", Phone = "0908888999", Email = "phuongnt@centercare.vn", CreatedAt = new DateTime(2025, 11, 1, 8, 0, 0, DateTimeKind.Utc) }
    ];

    private static List<Service> BuildServices() =>
    [
        new()
        {
            Id = "services/001",
            Name = "Bảo dưỡng và vệ sinh thiết bị toàn diện",
            Description = "Gói kiểm tra toàn diện, vệ sinh chi tiết linh kiện và tra keo tản nhiệt cao cấp",
            Price = 350000m,
            Category = "Bảo dưỡng định kỳ",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc)
        },
        new()
        {
            Id = "services/002",
            Name = "Chẩn đoán và xử lý sự cố bo mạch",
            Description = "Đo đạc nguồn, xử lý chạm chập IC và thay thế linh kiện điện tử",
            Price = 600000m,
            Category = "Sửa chữa phần cứng",
            CreatedAt = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc)
        },
        new() { Id = "services/003", Name = "Cài đặt lại hệ điều hành và phần mềm", Description = "Cài Windows/Linux, driver, phần mềm văn phòng và sao lưu dữ liệu trước khi cài", Price = 250000m, Category = "Cài đặt phần mềm", CreatedAt = new DateTime(2025, 11, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/004", Name = "Nâng cấp ổ cứng SSD và bộ nhớ RAM", Description = "Tư vấn cấu hình, lắp SSD/RAM, nhân bản dữ liệu và kiểm tra hiệu năng sau nâng cấp", Price = 200000m, Category = "Nâng cấp linh kiện", CreatedAt = new DateTime(2025, 11, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/005", Name = "Xử lý lỗi phần mềm và virus", Description = "Quét mã độc, gỡ phần mềm xung đột, tối ưu khởi động và khôi phục hệ thống", Price = 280000m, Category = "Sửa chữa phần mềm", CreatedAt = new DateTime(2025, 12, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/006", Name = "Sửa bàn phím, màn hình và bản lề laptop", Description = "Thay thế cụm bàn phím, màn hình, bản lề và kiểm tra đèn nền sau sửa", Price = 450000m, Category = "Sửa chữa phần cứng", CreatedAt = new DateTime(2025, 12, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/007", Name = "Vệ sinh laptop lấy ngay", Description = "Vệ sinh quạt, khe tản nhiệt và bàn phím trong ngày, phù hợp máy nóng và ồn", Price = 180000m, Category = "Bảo dưỡng định kỳ", CreatedAt = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/008", Name = "Thiết lập mạng, wifi và máy in văn phòng", Description = "Cấu hình router, chia sẻ máy in, xử lý mất kết nối và hướng dẫn sử dụng", Price = 320000m, Category = "Mạng và thiết bị ngoại vi", CreatedAt = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/009", Name = "Cứu dữ liệu ổ cứng và USB", Description = "Khôi phục dữ liệu mất do format, xóa nhầm hoặc lỗi file hệ thống (không gồm ổ hỏng cơ)", Price = 800000m, Category = "Cứu dữ liệu", CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc) },
        new() { Id = "services/010", Name = "Tư vấn và kiểm tra thiết bị trước khi mua", Description = "Đánh giá máy cũ, kiểm tra pin, màn hình, cổng kết nối và tư vấn cấu hình phù hợp nhu cầu", Price = 150000m, Category = "Tư vấn", CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc) }
    ];

    private static List<Appointment> BuildCanonicalAppointments() =>
    [
        new()
        {
            Id = "appointments/001",
            CustomerId = "customers/001",
            ServiceId = "services/001",
            AppointmentDate = new DateTime(2026, 2, 1, 9, 30, 0, DateTimeKind.Utc),
            Status = "Completed",
            Note = "Khách hàng yêu cầu kiểm tra kỹ quạt tản nhiệt do phát ra tiếng ồn khi chạy tải nặng",
            CreatedAt = new DateTime(2026, 1, 28, 14, 20, 0, DateTimeKind.Utc)
        },
        new()
        {
            Id = "appointments/002",
            CustomerId = "customers/002",
            ServiceId = "services/002",
            AppointmentDate = new DateTime(2026, 2, 5, 14, 0, 0, DateTimeKind.Utc),
            Status = "Confirmed",
            Note = "Khách báo máy tính khởi động không lên nguồn",
            CreatedAt = new DateTime(2026, 2, 2, 10, 15, 0, DateTimeKind.Utc)
        }
    ];

    private static ServiceHistory CanonicalHistory() => new()
    {
        Id = "servicehistories/001",
        AppointmentId = "appointments/001",
        CustomerId = "customers/001",
        ServiceId = "services/001",
        StaffId = "staffs/001",
        ServiceDate = new DateTime(2026, 2, 1, 10, 45, 0, DateTimeKind.Utc),
        Description = "Thực hiện vệ sinh quạt tản nhiệt, loại bỏ bụi bẩn, bôi trơn trục quạt và thay keo tản nhiệt gốm",
        Result = "Thiết bị hoạt động êm ái, nhiệt độ CPU giảm 15 độ C khi stress test, mọi chức năng ổn định",
        Note = "Đã bàn giao khách hàng test trực tiếp, khách hài lòng",
        CreatedAt = new DateTime(2026, 2, 1, 11, 0, 0, DateTimeKind.Utc)
    };

    private static Feedback CanonicalFeedback() => new()
    {
        Id = "feedbacks/001",
        CustomerId = "customers/001",
        AppointmentId = "appointments/001",
        Rating = 5,
        Comment = "Dịch vụ rất chu đáo và chuyên nghiệp! Nhân viên Mai tư vấn nhiệt tình, máy chạy mát và êm hẳn.",
        CreatedAt = new DateTime(2026, 2, 1, 15, 30, 0, DateTimeKind.Utc)
    };

    private static DateTime NextAppointmentDate(Random rng, DateTime start, DateTime end)
    {
        var days = Math.Max(1, (int)(end.Date - start.Date).TotalDays);
        DateTime day;
        do
        {
            day = start.Date.AddDays(rng.Next(days + 1));
        }
        while (day.DayOfWeek is DayOfWeek.Sunday);

        var hour = rng.Next(0, 10) == 0 ? rng.Next(8, 12) : rng.Next(8, 17);
        var minute = Pick(rng, new[] { 0, 15, 30, 45 });
        return DateTime.SpecifyKind(day.AddHours(hour).AddMinutes(minute), DateTimeKind.Utc);
    }

    private static int WeightedRating(Random rng)
    {
        var roll = rng.Next(100);
        return roll < 8 ? 1 : roll < 18 ? 2 : roll < 38 ? 3 : roll < 70 ? 4 : 5;
    }

    private static string FeedbackComment(Random rng, int rating) => rating switch
    {
        5 => Pick(rng, Feedback5),
        4 => Pick(rng, Feedback4),
        3 => Pick(rng, Feedback3),
        2 => Pick(rng, Feedback2),
        _ => Pick(rng, Feedback1)
    };

    private static string Id(string collection, int n) => $"{collection}/{n:D3}";

    private static T Pick<T>(Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];

    private static string Slug(string name)
    {
        var map = new Dictionary<char, char>
        {
            ['à'] = 'a', ['á'] = 'a', ['ạ'] = 'a', ['ả'] = 'a', ['ã'] = 'a',
            ['â'] = 'a', ['ầ'] = 'a', ['ấ'] = 'a', ['ậ'] = 'a', ['ẩ'] = 'a', ['ẫ'] = 'a',
            ['ă'] = 'a', ['ằ'] = 'a', ['ắ'] = 'a', ['ặ'] = 'a', ['ẳ'] = 'a', ['ẵ'] = 'a',
            ['è'] = 'e', ['é'] = 'e', ['ẹ'] = 'e', ['ẻ'] = 'e', ['ẽ'] = 'e',
            ['ê'] = 'e', ['ề'] = 'e', ['ế'] = 'e', ['ệ'] = 'e', ['ể'] = 'e', ['ễ'] = 'e',
            ['ì'] = 'i', ['í'] = 'i', ['ị'] = 'i', ['ỉ'] = 'i', ['ĩ'] = 'i',
            ['ò'] = 'o', ['ó'] = 'o', ['ọ'] = 'o', ['ỏ'] = 'o', ['õ'] = 'o',
            ['ô'] = 'o', ['ồ'] = 'o', ['ố'] = 'o', ['ộ'] = 'o', ['ổ'] = 'o', ['ỗ'] = 'o',
            ['ơ'] = 'o', ['ờ'] = 'o', ['ớ'] = 'o', ['ợ'] = 'o', ['ở'] = 'o', ['ỡ'] = 'o',
            ['ù'] = 'u', ['ú'] = 'u', ['ụ'] = 'u', ['ủ'] = 'u', ['ũ'] = 'u',
            ['ư'] = 'u', ['ừ'] = 'u', ['ứ'] = 'u', ['ự'] = 'u', ['ử'] = 'u', ['ữ'] = 'u',
            ['ỳ'] = 'y', ['ý'] = 'y', ['ỵ'] = 'y', ['ỷ'] = 'y', ['ỹ'] = 'y',
            ['đ'] = 'd'
        };
        var chars = name.ToLowerInvariant().Select(ch =>
        {
            if (ch is ' ' or '.') return '.';
            return map.TryGetValue(ch, out var ascii) ? ascii : ch;
        }).Where(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.').ToArray();
        var slug = new string(chars).Trim('.');
        while (slug.Contains("..", StringComparison.Ordinal))
            slug = slug.Replace("..", ".", StringComparison.Ordinal);
        return slug;
    }

    private static readonly (string Name, string[] Districts, string[] Wards)[] Cities =
    [
        ("TP. Hồ Chí Minh", ["Quận 1", "Quận 3", "Quận 5", "Quận 7", "Quận 10", "Bình Thạnh", "Tân Bình", "Gò Vấp", "Thủ Đức", "Phú Nhuận"], ["Bến Nghé", "Phường 4", "Phường 7", "Tân Phong", "Linh Chiểu", "Phường 15"]),
        ("Hà Nội", ["Hoàn Kiếm", "Ba Đình", "Đống Đa", "Cầu Giấy", "Hai Bà Trưng", "Thanh Xuân", "Long Biên"], ["Hàng Trống", "Ô Chợ Dừa", "Dịch Vọng", "Khương Trung", "Phúc Tân"]),
        ("Đà Nẵng", ["Hải Châu", "Thanh Khê", "Sơn Trà", "Ngũ Hành Sơn", "Liên Chiểu"], ["Thạch Thang", "Xuân Hà", "Mỹ An", "Hòa Minh"]),
        ("Cần Thơ", ["Ninh Kiều", "Bình Thủy", "Cái Răng"], ["An Cư", "An Hòa", "Hưng Lợi"]),
        ("Hải Phòng", ["Hồng Bàng", "Lê Chân", "Ngô Quyền"], ["Hoàng Văn Thụ", "Dư Hàng", "Máy Tơ"]),
        ("Huế", ["Thuận Hóa", "Phú Xuân"], ["Phú Hội", "Vỹ Dạ", "Thuận Hòa"]),
        ("Nha Trang", ["Nha Trang"], ["Lộc Thọ", "Vĩnh Hải", "Vĩnh Nguyên"]),
        ("Biên Hòa", ["Biên Hòa"], ["Tân Tiến", "Quang Vinh", "Thống Nhất"]),
        ("Vũng Tàu", ["Vũng Tàu"], ["Phường 1", "Phường 2", "Thắng Tam"]),
        ("Buôn Ma Thuột", ["Buôn Ma Thuột"], ["Tân Lợi", "Tân Thành", "Ea Tam"])
    ];

    private static readonly string[] Streets =
    [
        "Nguyễn Huệ", "Lê Lợi", "Trần Hưng Đạo", "Hai Bà Trưng", "Lý Thường Kiệt",
        "Cách Mạng Tháng Tám", "Nguyễn Thị Minh Khai", "Điện Biên Phủ", "Pasteur",
        "Võ Văn Tần", "Nguyễn Trãi", "Hoàng Hoa Thám", "Phan Đình Phùng", "Hùng Vương"
    ];

    private static readonly string[] AppointmentNotes =
    [
        "Khách muốn lấy máy trong ngày nếu có thể.",
        "Mang theo sạc và túi đựng máy.",
        "Máy đã được sao lưu dữ liệu quan trọng ở nhà.",
        "Khách phản ánh máy nóng bất thường khi mở nhiều tab.",
        "Cần báo giá linh kiện trước khi thay.",
        "Hẹn đúng giờ vì khách sắp đi công tác.",
        "Nhờ kiểm tra thêm cổng USB bị lỏng.",
        "Khách là khách quen, ưu tiên kỹ thuật viên đã sửa lần trước nếu rảnh.",
        "Máy dùng cho việc học online, cần xong trước cuối tuần.",
        "Không cần giữ linh kiện cũ.",
        "Yêu cầu xuất hóa đơn công ty.",
        "Gọi điện trước 30 phút khi xong việc."
    ];

    private static readonly string[] HistoryDescriptions =
    [
        "Vệ sinh hệ thống tản nhiệt, kiểm tra keo tản nhiệt và chạy thử tải.",
        "Chẩn đoán bo mạch, đo nguồn và thay linh kiện hỏng.",
        "Sao lưu dữ liệu, cài lại hệ điều hành, driver và phần mềm văn phòng.",
        "Nâng cấp SSD/RAM, nhân bản ổ đĩa và kiểm tra điểm benchmark.",
        "Quét mã độc, gỡ phần mềm rác và tối ưu chương trình khởi động.",
        "Thay cụm bàn phím/màn hình, căn chỉnh bản lề và kiểm tra đèn nền.",
        "Cấu hình lại mạng, wifi và chia sẻ máy in trong văn phòng.",
        "Khôi phục dữ liệu đã mất, xuất ra ổ rời và đối chiếu dung lượng.",
        "Kiểm tra tổng quát thiết bị, đo pin và cổng kết nối.",
        "Xử lý sự cố không lên nguồn, thay adapter và kiểm tra jack nguồn."
    ];

    private static readonly string[] HistoryResults =
    [
        "Máy hoạt động ổn định, nhiệt độ trong ngưỡng an toàn khi stress test.",
        "Đã khắc phục sự cố, các chức năng cơ bản hoạt động bình thường.",
        "Hệ thống khởi động nhanh hơn, phần mềm chạy đúng yêu cầu khách hàng.",
        "Dữ liệu đã được kiểm tra sau thao tác, khách xác nhận đủ file quan trọng.",
        "Linh kiện mới nhận diện đúng, bảo hành kích hoạt theo phiếu.",
        "Mạng và thiết bị ngoại vi kết nối ổn định sau khi cấu hình lại.",
        "Vẫn còn hiện tượng nhẹ khi tải rất cao, đã hướng dẫn cách sử dụng.",
        "Hoàn tất trong thời gian hẹn, máy sạch và chạy êm."
    ];

    private static readonly string[] HistoryNotes =
    [
        "Đã bàn giao và hướng dẫn khách kiểm tra trực tiếp.",
        "Nhắc khách mang máy bảo dưỡng lại sau 6 tháng.",
        "Cần theo dõi thêm nếu triệu chứng tái diễn.",
        "Khách đồng ý thay linh kiện theo báo giá.",
        "Không phát sinh chi phí ngoài gói đã chốt.",
        "Đã gửi biên bản nghiệm thu qua email."
    ];

    private static readonly string[] Feedback5 =
    [
        "Rất hài lòng, nhân viên tư vấn rõ ràng và máy chạy tốt ngay sau khi nhận.",
        "Làm nhanh, sạch sẽ, sẽ quay lại lần sau.",
        "Chuyên nghiệp và đúng hẹn, đáng tin cậy.",
        "Kỹ thuật viên giải thích dễ hiểu, giá minh bạch."
    ];

    private static readonly string[] Feedback4 =
    [
        "Dịch vụ tốt, chỉ hơi chờ lâu hơn dự kiến một chút.",
        "Máy đã ổn, nhân viên thân thiện. Sẽ giới thiệu bạn bè.",
        "Hài lòng với kết quả, lần sau muốn được nhắc lịch bảo dưỡng.",
        "Sửa đúng bệnh, giá hợp lý."
    ];

    private static readonly string[] Feedback3 =
    [
        "Máy dùng được nhưng vẫn còn nóng nhẹ khi chơi game.",
        "Ổn, chưa thực sự nhanh như mong đợi.",
        "Nhân viên hỗ trợ tạm được, thời gian chờ hơi dài.",
        "Kết quả chấp nhận được, sẽ theo dõi thêm vài ngày."
    ];

    private static readonly string[] Feedback2 =
    [
        "Phải chờ khá lâu, kết quả chưa như trao đổi ban đầu.",
        "Máy hết lỗi này lại xuất hiện tiếng ồn khác.",
        "Cần giải thích kỹ hơn về chi phí phát sinh."
    ];

    private static readonly string[] Feedback1 =
    [
        "Không hài lòng vì phải mang máy tới lần nữa.",
        "Thời gian sửa kéo dài, ít được cập nhật tiến độ.",
        "Kết quả chưa đạt, mong được hỗ trợ lại."
    ];
}

public sealed record SeedBundle(
    List<Customer> Customers,
    List<Staff> Staffs,
    List<Service> Services,
    List<Appointment> Appointments,
    List<ServiceHistory> Histories,
    List<Feedback> Feedbacks);
