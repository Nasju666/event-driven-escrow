using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace EscrowMarketplace.IntegrationTests;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL 16 container (Docker required), so the GiST
/// exclusion constraint, xmin concurrency tokens, the EF transactional outbox and the EF saga
/// repository are all exercised for real. MassTransit runs on its in-memory transport (no RabbitMQ),
/// with the delayed scheduler handling saga timeouts.
/// </summary>
public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Program.cs reads configuration while building services, so supply it through
        // environment variables (read by WebApplication.CreateBuilder) as well as UseSetting.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
    }

    /// <summary>An HttpClient signed in as a seeded demo user.</summary>
    public async Task<HttpClient> ClientForAsync(string email)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/token", new { email });
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(TestJson.Options);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token);
        return client;
    }

    private record TokenResponse(string Token);
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<CustomWebApplicationFactory>
{
    public const string Name = "api";
}

public static class Users
{
    public const string Buyer = "marcus@soundwave.io";
    public const string SecondBuyer = "priya@soundwave.io";
    public const string Provider = "sophia@vancestudios.com";
    public const string Admin = "admin@marketplace.local";
    public const string OtherTenantBuyer = "lena@northwind.test";
}

public static class Resources
{
    public static readonly Guid LiveRoom = Guid.Parse("55555555-5555-5555-5555-555555555555");     // $75/h
    public static readonly Guid MixingSuite = Guid.Parse("66666666-6666-6666-6666-666666666666");  // $120/h
    public static readonly Guid VocalBooth = Guid.Parse("77777777-7777-7777-7777-777777777777");   // $45/h
    public static readonly Guid NorthwindRoom = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
}

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class ApiExtensions
{
    private static int _slotCounter;

    /// <summary>
    /// A unique future time slot per call, so tests never collide on the same resource
    /// (each test class shares one database).
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) UniqueSlot(double hours = 1)
    {
        var n = Interlocked.Increment(ref _slotCounter);
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(30 + n).AddHours(9);
        return (start, start.AddHours(hours));
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Options))!;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, TestJson.Options);

    public static async Task<BookingDto> CreateBookingAsync(this HttpClient buyer, Guid resourceId, double hours = 1)
    {
        var (start, end) = UniqueSlot(hours);
        var response = await buyer.PostJsonAsync("/api/bookings", new { resourceId, startTime = start, endTime = end });
        return await response.ReadAsync<BookingDto>();
    }

    public static async Task<BookingDto> ActAsync(this HttpClient client, Guid bookingId, string action, object? body = null)
    {
        var response = body is null
            ? await client.PostAsync($"/api/bookings/{bookingId}/{action}", null)
            : await client.PostJsonAsync($"/api/bookings/{bookingId}/{action}", body);
        return await response.ReadAsync<BookingDto>();
    }

    public static async Task<BookingDto> GetBookingAsync(this HttpClient client, Guid bookingId) =>
        await (await client.GetAsync($"/api/bookings/{bookingId}")).ReadAsync<BookingDto>();

    public static async Task<decimal> WalletAsync(this HttpClient client) =>
        (await (await client.GetAsync("/api/me")).ReadAsync<CurrentUserDto>()).WalletBalance;

    /// <summary>Polls until the booking reaches a status. Used to observe async saga-driven changes.</summary>
    public static async Task<BookingDto> WaitForStatusAsync(
        this HttpClient client, Guid bookingId, BookingStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        BookingDto current;
        do
        {
            current = await client.GetBookingAsync(bookingId);
            if (current.Status == expected) return current;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Booking {bookingId} stayed {current.Status}; expected {expected} within {timeout}.");
    }
}
