using CustomerCareRavenDB.Data;
using CustomerCareRavenDB.Data.Validation;
using CustomerCareRavenDB.Indexes;
using CustomerCareRavenDB.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.ServerWide.Operations;

namespace CustomerCareRavenDB.Tests;

public class IntegrationTests : IAsyncLifetime
{
    private readonly string _testDbName = $"Test_Integration_{Guid.NewGuid():N}";
    private IDocumentStore _store = null!;
    private RavenDbContext _context = null!;
    private DatabaseSeeder _seeder = null!;

    public async Task InitializeAsync()
    {
        var settings = Options.Create(new RavenSettings
        {
            Urls = [TestSettings.RavenUrl],
            DatabaseName = _testDbName
        });

        _context = new RavenDbContext(settings, NullLogger<RavenDbContext>.Instance);
        _store = _context.Store;

        var validator = new DocumentValidator();
        _seeder = new DatabaseSeeder(_context, validator, NullLogger<DatabaseSeeder>.Instance);

        // Đảm bảo database test được tạo
        await _context.EnsureDatabaseExistsAsync(_testDbName);

        // Đăng ký static index ServiceHistories_ByCustomerAndDate
        await IndexCreation.CreateIndexesAsync(
            typeof(ServiceHistories_ByCustomerAndDate).Assembly,
            _store);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(_testDbName, hardDelete: true));
        }
        catch
        {
            // Bỏ qua lỗi dọn dẹp
        }
        finally
        {
            _context.Dispose();
        }
    }

    [Fact]
    public async Task Seeder_InitializesCompleteBusinessWorkflow_AndIsIdempotent()
    {
        // Act 1: Lần nạp đầu tiên
        var firstRunResult = await _seeder.SeedAsync();
        Assert.True(firstRunResult, "Lần seed đầu tiên phải trả về true khi nạp thành công.");

        // Kiểm tra dữ liệu được nạp vào
        using (var session = _context.OpenAsyncSession())
        {
            var customer = await session.LoadAsync<Customer>("customers/001");
            Assert.NotNull(customer);
            Assert.Equal("Nguyễn Văn An", customer.FullName);
            Assert.NotNull(customer.Address);
            Assert.Equal("123 Nguyễn Huệ", customer.Address.Street);

            var staff = await session.LoadAsync<Staff>("staffs/001");
            Assert.NotNull(staff);
            Assert.Equal("Trần Thị Mai", staff.FullName);

            var service = await session.LoadAsync<Service>("services/001");
            Assert.NotNull(service);
            Assert.Equal(350000m, service.Price);

            var appointment = await session.LoadAsync<Appointment>("appointments/001");
            Assert.NotNull(appointment);
            Assert.Equal(customer.Id, appointment.CustomerId);
            Assert.Equal(service.Id, appointment.ServiceId);

            var history = await session.LoadAsync<ServiceHistory>("servicehistories/001");
            Assert.NotNull(history);
            Assert.Equal(appointment.Id, history.AppointmentId);
            Assert.Equal(customer.Id, history.CustomerId);
            Assert.Equal(service.Id, history.ServiceId);
            Assert.Equal(staff.Id, history.StaffId);

            var feedback = await session.LoadAsync<Feedback>("feedbacks/001");
            Assert.NotNull(feedback);
            Assert.Equal(customer.Id, feedback.CustomerId);
            Assert.Equal(appointment.Id, feedback.AppointmentId);
            Assert.Equal(5, feedback.Rating);
        }

        // Act 2: Lần nạp thứ hai (kiểm tra tính Idempotent)
        var secondRunResult = await _seeder.SeedAsync();
        Assert.False(secondRunResult, "Lần seed thứ hai phải trả về false và không được nạp đè dữ liệu.");
    }

    [Fact]
    public async Task Seeder_LoadsExpectedCounts_AndKeepsReferencesConsistent()
    {
        Assert.True(await _seeder.SeedAsync());
        var expected = DatabaseSeeder.BuildSeedData();

        using var session = _context.OpenAsyncSession();
        Assert.Equal(SeedCatalog.CustomerCount, await session.Query<Customer>().CountAsync());
        Assert.Equal(SeedCatalog.StaffCount, await session.Query<Staff>().CountAsync());
        Assert.Equal(SeedCatalog.ServiceCount, await session.Query<Service>().CountAsync());
        Assert.Equal(SeedCatalog.AppointmentCount, await session.Query<Appointment>().CountAsync());
        Assert.Equal(expected.Histories.Count, await session.Query<ServiceHistory>().CountAsync());
        Assert.Equal(expected.Feedbacks.Count, await session.Query<Feedback>().CountAsync());

        Assert.InRange(expected.Histories.Count, 80, 160);
        Assert.InRange(expected.Feedbacks.Count, 50, 140);

        var customers = (await session.Query<Customer>().Take(500).ToListAsync()).ToDictionary(c => c.Id);
        var staffs = (await session.Query<Staff>().Take(100).ToListAsync()).Select(s => s.Id).ToHashSet();
        var services = (await session.Query<Service>().Take(100).ToListAsync()).Select(s => s.Id).ToHashSet();
        var appointments = (await session.Query<Appointment>().Take(500).ToListAsync()).ToDictionary(a => a.Id);

        Assert.Contains(customers.Values, c => c.Gender == CustomerGender.Male);
        Assert.Contains(customers.Values, c => c.Gender == CustomerGender.Female);
        Assert.Contains(customers.Values, c => c.Gender == CustomerGender.Other);
        Assert.Contains(appointments.Values, a => a.Status == "Pending");
        Assert.Contains(appointments.Values, a => a.Status == "Confirmed");
        Assert.Contains(appointments.Values, a => a.Status == "Completed");
        Assert.Contains(appointments.Values, a => a.Status == "Cancelled");
        Assert.True(appointments.Values.Count(a => a.AppointmentDate > SeedCatalog.AsOfUtc) >= 10);
        Assert.True(appointments.Values.Count(a => a.AppointmentDate < SeedCatalog.AsOfUtc.AddMonths(-6)) >= 10);

        foreach (var appointment in appointments.Values)
        {
            Assert.True(customers.ContainsKey(appointment.CustomerId), appointment.Id);
            Assert.Contains(appointment.ServiceId, services);
            Assert.Contains(appointment.Status, new[] { "Pending", "Confirmed", "Completed", "Cancelled" });
        }

        var histories = await session.Query<ServiceHistory>().Take(500).ToListAsync();
        Assert.Equal(appointments.Values.Count(a => a.Status == "Completed"), histories.Count);
        foreach (var history in histories)
        {
            var appointment = appointments[history.AppointmentId];
            Assert.Equal("Completed", appointment.Status);
            Assert.Equal(appointment.CustomerId, history.CustomerId);
            Assert.Equal(appointment.ServiceId, history.ServiceId);
            Assert.Contains(history.StaffId, staffs);
        }

        var feedbacks = await session.Query<Feedback>().Take(500).ToListAsync();
        foreach (var feedback in feedbacks)
        {
            var appointment = appointments[feedback.AppointmentId];
            Assert.Equal(appointment.CustomerId, feedback.CustomerId);
            Assert.InRange(feedback.Rating, 1, 5);
        }

        Assert.Equal("Nguyễn Văn An", customers["customers/001"].FullName);
        Assert.Single(appointments.Values, a => a.CustomerId == "customers/001");
        Assert.Single(histories, h => h.CustomerId == "customers/001");
    }

    [Fact]
    public async Task StaticIndex_ServiceHistories_ByCustomerAndDate_CanBeQueried()
    {
        // Nạp dữ liệu mẫu
        await _seeder.SeedAsync();

        using var session = _context.OpenAsyncSession();

        // Chờ index xử lý xong (Non-stale)
        var histories = await session.Query<ServiceHistory, ServiceHistories_ByCustomerAndDate>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(x => x.CustomerId == "customers/001")
            .OrderByDescending(x => x.ServiceDate)
            .ToListAsync();

        Assert.NotEmpty(histories);
        Assert.All(histories, h => Assert.Equal("customers/001", h.CustomerId));
    }

    [Fact]
    public async Task RqlQuery_WithLoad_RetrievesReferencedCustomer()
    {
        await _seeder.SeedAsync();

        using var session = _context.OpenAsyncSession();

        // RQL Query #10: sử dụng load để lấy thông tin Customer từ Appointment
        var rql = @"
            from Appointments as a
            load a.CustomerId as c
            select {
                AppointmentId: id(a),
                CustomerName: c.FullName,
                CustomerPhone: c.Phone,
                Status: a.Status
            }";

        var results = await session.Advanced.AsyncRawQuery<dynamic>(rql)
            .ToListAsync();

        Assert.NotEmpty(results);
    }
}
