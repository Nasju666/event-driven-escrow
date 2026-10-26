using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Infrastructure.Consumers;
using EscrowMarketplace.Infrastructure.Jobs;
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
        var connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Host=localhost;Port=5432;Database=EscrowMarketplaceDb;Username=postgres;Password=postgres";

        // 1. EF Core DbContext with Npgsql & btree_gist
        services.AddDbContext<MarketplaceDbContext>(options =>
        {
            if (configuration.GetValue<bool>("UseInMemoryDatabase"))
            {
                options.UseInMemoryDatabase("EscrowMarketplaceTestDb");
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

        // 2. Multi-tenant Context
        services.AddScoped<CurrentTenantService>();
        services.AddScoped<ICurrentTenantService>(sp => sp.GetRequiredService<CurrentTenantService>());

        // 3. Dapper Analytics & Core Services
        services.AddScoped<IAnalyticsRepository, DapperAnalyticsRepository>();
        services.AddScoped<IEscrowPaymentGateway, MockEscrowPaymentGateway>();
        services.AddScoped<IBookingNotifier, BookingNotifier>();

        // 4. Redis Distributed Cache
        var redisConn = configuration.GetConnectionString("Redis") ?? "localhost:6379";
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConn;
            options.InstanceName = "EscrowMarketplace:";
        });
        services.AddSingleton<ICacheService, RedisCacheService>();

        // 5. MassTransit with Transactional EF Core Outbox & Saga State Machine
        services.AddMassTransit(busConfig =>
        {
            busConfig.SetKebabCaseEndpointNameFormatter();

            // Register consumers
            busConfig.AddConsumer<EscrowStatusChangedSignalRConsumer>();
            busConfig.AddConsumer<BookingEventAuditConsumer>();

            // Register Escrow Saga State Machine with Optimistic Concurrency (xmin)
            busConfig.AddSagaStateMachine<EscrowStateMachine, EscrowBookingState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ExistingDbContext<MarketplaceDbContext>();
                    r.UsePostgres();
                    r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                });

            // Register Transactional Outbox
            busConfig.AddEntityFrameworkOutbox<MarketplaceDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox(bo =>
                {
                    bo.MessageDeliveryLimit = 100;
                    bo.MessageDeliveryTimeout = TimeSpan.FromSeconds(45);
                });
                o.QueryDelay = TimeSpan.FromSeconds(5);
            });

            // Endpoint callbacks configuring retry and EF Core consumer outbox
            busConfig.AddConfigureEndpointsCallback((context, name, cfg) =>
            {
                cfg.UseMessageRetry(r => r.Immediate(5));
                cfg.UseEntityFrameworkOutbox<MarketplaceDbContext>(context);
            });

            // Transport: RabbitMQ if configured, else In-Memory with full outbox features
            var rabbitMqHost = configuration["RabbitMQ:Host"];
            if (!string.IsNullOrEmpty(rabbitMqHost))
            {
                busConfig.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(rabbitMqHost, h =>
                    {
                        h.Username(configuration["RabbitMQ:Username"] ?? "guest");
                        h.Password(configuration["RabbitMQ:Password"] ?? "guest");
                    });

                    cfg.ConfigureEndpoints(context);
                });
            }
            else
            {
                busConfig.UsingInMemory((context, cfg) =>
                {
                    cfg.ConfigureEndpoints(context);
                });
            }
        });

        // 6. SignalR
        var signalR = services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = true;
        });

        var signalRRedis = configuration["SignalR:Redis"];
        if (!string.IsNullOrEmpty(signalRRedis))
        {
            signalR.AddStackExchangeRedis(signalRRedis);
        }

        // 7. Hangfire Background Jobs
        if (!configuration.GetValue<bool>("UseInMemoryDatabase"))
        {
            services.AddHangfire(config =>
            {
                config.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                      .UseSimpleAssemblyNameTypeSerializer()
                      .UseRecommendedSerializerSettings()
                      .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString));
            });
            services.AddHangfireServer(opt =>
            {
                opt.WorkerCount = Math.Max(Environment.ProcessorCount * 2, 4);
            });
        }

        services.AddScoped<LedgerReconciliationJob>();
        services.AddScoped<EscrowTimeoutCleanupJob>();

        return services;
    }
}
