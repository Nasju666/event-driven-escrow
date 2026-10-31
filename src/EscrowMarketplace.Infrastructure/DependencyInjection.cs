using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Settings;
using EscrowMarketplace.Infrastructure.Consumers;
using EscrowMarketplace.Infrastructure.Jobs;
using EscrowMarketplace.Infrastructure.Messaging;
using EscrowMarketplace.Infrastructure.Persistence;
using EscrowMarketplace.Infrastructure.Repositories;
using EscrowMarketplace.Infrastructure.Saga;
using EscrowMarketplace.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EscrowMarketplace.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var useInMemory = configuration.GetValue<bool>("UseInMemoryDatabase");
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (!useInMemory && string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required unless UseInMemoryDatabase is true.");

        services.Configure<EscrowWorkflowOptions>(configuration.GetSection(EscrowWorkflowOptions.SectionName));

        // ── 1. EF Core ──
        services.AddDbContext<MarketplaceDbContext>(options =>
        {
            if (useInMemory)
            {
                options.UseInMemoryDatabase("EscrowMarketplaceDemoDb");
                options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
            }
            else
            {
                options.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsAssembly(typeof(MarketplaceDbContext).Assembly.FullName);
                    npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                });
            }
        });

        services.AddScoped<IMarketplaceDbContext>(sp => sp.GetRequiredService<MarketplaceDbContext>());
        services.AddScoped<IReadDbContext, ReadDbContext>();
        services.AddScoped<MarketplaceDbContextInitializer>();

        // ── 2. Request / message context (who and which tenant) ──
        services.AddScoped<CurrentTenantService>();
        services.AddScoped<ICurrentTenantService>(sp => sp.GetRequiredService<CurrentTenantService>());
        services.AddScoped<CurrentActor>();
        services.AddScoped<ICurrentActor>(sp => sp.GetRequiredService<CurrentActor>());

        services.AddScoped<IAnalyticsRepository, DapperAnalyticsRepository>();
        services.AddScoped<IEscrowWorkflowReader, EscrowWorkflowReader>();
        services.AddScoped<IBookingNotifier, BookingNotifier>();

        // Resolved lazily by MarketplaceDbContext.SaveChangesAsync. The scoped IPublishEndpoint is
        // the EF bus outbox in web requests and the consume context (consumer outbox) in consumers.
        services.AddScoped<IDomainEventDispatcher>(sp =>
            new MassTransitDomainEventDispatcher(sp.GetRequiredService<IPublishEndpoint>(), usesTransactionalOutbox: !useInMemory));

        // ── 3. MassTransit: outbox, saga, consumers, delayed scheduling ──
        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();

            // Saga timeouts are scheduled with the transport's delayed delivery
            // (RabbitMQ delayed-message-exchange plugin, or the in-memory transport).
            bus.AddDelayedMessageScheduler();

            bus.AddConsumer<WorkflowCommandConsumer>();
            bus.AddConsumer<RealtimeNotificationConsumer>();

            // One message at a time on the saga endpoint: retries happen in place, so events are
            // applied in the order the outbox delivered them. At higher volume you would partition
            // by BookingId instead (UsePartitioner) to keep per-booking ordering with parallelism.
            var saga = bus.AddSagaStateMachine<EscrowStateMachine, EscrowBookingState>()
                .Endpoint(e => e.ConcurrentMessageLimit = 1);
            if (useInMemory)
            {
                saga.InMemoryRepository();
            }
            else
            {
                saga.EntityFrameworkRepository(r =>
                {
                    r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                    r.ExistingDbContext<MarketplaceDbContext>();
                    r.UsePostgres();
                });

                bus.AddEntityFrameworkOutbox<MarketplaceDbContext>(o =>
                {
                    o.UsePostgres();
                    o.UseBusOutbox();
                    o.QueryDelay = TimeSpan.FromSeconds(1);
                    o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
                });
            }

            bus.AddConfigureEndpointsCallback((context, name, cfg) =>
            {
                // Covers optimistic-concurrency conflicts on the saga row and the short window
                // where an event can overtake the BookingSlotReservedEvent that creates the saga.
                cfg.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000, 5000));

                if (useInMemory)
                    cfg.UseInMemoryOutbox(context);
                else
                    cfg.UseEntityFrameworkOutbox<MarketplaceDbContext>(context);
            });

            var rabbitMqHost = configuration["RabbitMQ:Host"];
            if (!string.IsNullOrWhiteSpace(rabbitMqHost))
            {
                bus.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(rabbitMqHost, h =>
                    {
                        h.Username(configuration["RabbitMQ:Username"] ?? "guest");
                        h.Password(configuration["RabbitMQ:Password"] ?? "guest");
                    });

                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            }
            else
            {
                bus.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            }
        });

        // ── 4. SignalR (optional Redis backplane for multiple API instances) ──
        var signalR = services.AddSignalR();
        var signalRRedis = configuration["SignalR:Redis"];
        if (!string.IsNullOrWhiteSpace(signalRRedis))
        {
            signalR.AddStackExchangeRedis(signalRRedis);
        }

        // ── 5. Hangfire: nightly reconciliation only ──
        if (IsHangfireEnabled(configuration))
        {
            services.AddHangfire(config =>
            {
                config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                      .UseSimpleAssemblyNameTypeSerializer()
                      .UseRecommendedSerializerSettings()
                      .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString));
            });
            services.AddHangfireServer();
        }

        services.AddScoped<LedgerReconciliationJob>();

        return services;
    }

    public static bool IsHangfireEnabled(IConfiguration configuration) =>
        !configuration.GetValue<bool>("UseInMemoryDatabase") && configuration.GetValue("Hangfire:Enabled", true);
}
