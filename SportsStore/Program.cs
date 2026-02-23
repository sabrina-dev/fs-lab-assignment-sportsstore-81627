using Microsoft.EntityFrameworkCore;
using SportsStore.Models;
using SportsStore.Services;
using Microsoft.AspNetCore.Identity;
using Serilog;
using Serilog.Context;
using Stripe;
using Stripe.Checkout;

// ── Bootstrap logger catches startup failures before host is built ──────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("SportsStore starting up on machine {MachineName}", Environment.MachineName);

    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog: replace default logging with Serilog, reading from config ──
    builder.Host.UseSerilog((ctx, services, cfg) =>
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext());

    // ── MVC / Razor / Blazor ────────────────────────────────────────────────
    builder.Services.AddControllersWithViews();
    builder.Services.AddRazorPages();
    builder.Services.AddServerSideBlazor();

    // ── EF Core – Store ─────────────────────────────────────────────────────
    builder.Services.AddDbContext<StoreDbContext>(opts =>
        opts.UseSqlServer(
            builder.Configuration["ConnectionStrings:SportsStoreConnection"]));

    builder.Services.AddScoped<IStoreRepository, EFStoreRepository>();
    builder.Services.AddScoped<IOrderRepository, EFOrderRepository>();

    // ── Session / Cart ───────────────────────────────────────────────────────
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession();
    builder.Services.AddScoped<Cart>(sp => SessionCart.GetCart(sp));
    builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

    // ── Identity ─────────────────────────────────────────────────────────────
    builder.Services.AddDbContext<AppIdentityDbContext>(options =>
        options.UseSqlServer(
            builder.Configuration["ConnectionStrings:IdentityConnection"]));

    builder.Services.AddIdentity<IdentityUser, IdentityRole>()
        .AddEntityFrameworkStores<AppIdentityDbContext>();

    // ── Stripe ───────────────────────────────────────────────────────────────
    builder.Services.Configure<StripeSettings>(
        builder.Configuration.GetSection("Stripe"));
    builder.Services.AddScoped<IPaymentService, StripePaymentService>();

    var app = builder.Build();

    // ── Serilog request logging (structured HTTP access log) ─────────────────
    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            diagCtx.Set("RequestHost", httpCtx.Request.Host.Value ?? string.Empty);
            diagCtx.Set("UserAgent", httpCtx.Request.Headers.UserAgent.ToString());
        };
    });

    // ── Global exception handler with structured logging ─────────────────────
    if (app.Environment.IsProduction())
    {
        app.UseExceptionHandler("/error");
    }
    else
    {
        app.UseDeveloperExceptionPage();
    }

    app.UseRequestLocalization(opts =>
    {
        opts.AddSupportedCultures("en-US")
            .AddSupportedUICultures("en-US")
            .SetDefaultCulture("en-US");
    });

    app.UseStaticFiles();
    app.UseSession();

    // ── Correlation ID middleware ─────────────────────────────────────────────
    app.Use(async (context, next) =>
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N")[..8];
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next();
        }
    });

    app.UseAuthentication();
    app.UseAuthorization();

    // ── Stripe webhook (raw body required — must be before routing) ───────────
    app.MapPost("/stripe/webhook", async (HttpContext httpCtx,
        IOrderRepository orderRepo,
        ILogger<Program> logger,
        IConfiguration config) =>
    {
        var json = await new StreamReader(httpCtx.Request.Body).ReadToEndAsync();
        var webhookSecret = config["Stripe:WebhookSecret"] ?? string.Empty;

        try
        {
            var stripeEvent = EventUtility.ConstructEvent(
                json,
                httpCtx.Request.Headers["Stripe-Signature"],
                webhookSecret,
                throwOnApiVersionMismatch: false);

            logger.LogInformation(
                "Stripe webhook received. EventType={EventType} EventId={EventId}",
                stripeEvent.Type, stripeEvent.Id);

            if (stripeEvent.Type == EventTypes.CheckoutSessionCompleted)
            {
                var session = stripeEvent.Data.Object as Session;
                if (session is not null)
                {
                    var orderIdStr = session.Metadata?.GetValueOrDefault("OrderId");
                    if (int.TryParse(orderIdStr, out var orderId))
                    {
                        var order = orderRepo.Orders
                            .FirstOrDefault(o => o.OrderID == orderId);

                        if (order is not null)
                        {
                            order.PaymentStatus     = "Paid";
                            order.PaymentIntentId   = session.PaymentIntentId;
                            order.CheckoutSessionId = session.Id;
                            order.PaidAt            = DateTime.UtcNow;
                            order.PaidAmount        = session.AmountTotal.HasValue
                                ? session.AmountTotal.Value / 100m
                                : 0m;
                            orderRepo.SaveOrder(order);

                            logger.LogInformation(
                                "Payment confirmed. OrderId={OrderId} SessionId={SessionId} " +
                                "PaymentIntentId={PaymentIntentId} Amount={Amount}",
                                orderId, session.Id, session.PaymentIntentId,
                                order.PaidAmount);
                        }
                        else
                        {
                            logger.LogWarning(
                                "Webhook received for unknown OrderId={OrderId}", orderId);
                        }
                    }
                }
            }

            return Results.Ok();
        }
        catch (StripeException ex)
        {
            logger.LogError(ex,
                "Stripe webhook signature validation failed. Error={Error}", ex.Message);
            return Results.BadRequest("Webhook signature invalid");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception processing Stripe webhook");
            return Results.StatusCode(500);
        }
    });

    // ── MVC routes ────────────────────────────────────────────────────────────
    app.MapControllerRoute("catpage",
        "{category}/Page{productPage:int}",
        new { Controller = "Home", action = "Index" });

    app.MapControllerRoute("page", "Page{productPage:int}",
        new { Controller = "Home", action = "Index", productPage = 1 });

    app.MapControllerRoute("category", "{category}",
        new { Controller = "Home", action = "Index", productPage = 1 });

    app.MapControllerRoute("pagination",
        "Products/Page{productPage}",
        new { Controller = "Home", action = "Index", productPage = 1 });

    app.MapDefaultControllerRoute();
    app.MapRazorPages();
    app.MapBlazorHub();
    app.MapFallbackToPage("/admin/{*catchall}", "/Admin/Index");

    SeedData.EnsurePopulated(app);
    IdentitySeedData.EnsurePopulated(app);

    Log.Information(
        "SportsStore started. Environment={Environment} Framework={Framework}",
        app.Environment.EnvironmentName,
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SportsStore terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
