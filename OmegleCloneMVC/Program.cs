using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OmegleCloneMVC.Data;
using OmegleCloneMVC.Hubs;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Stripe key
Stripe.StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

// DB
builder.Services.AddDbContext<OmegleCloneMVCContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("OmegleCloneMVCContext")));

// AUTH: "Smart" schema bira JWT ako postoji Bearer, inače Cookie.
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "Smart";
    options.DefaultChallengeScheme = "Smart";
})
.AddPolicyScheme("Smart", "JWT or Cookie", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrWhiteSpace(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return JwtBearerDefaults.AuthenticationScheme;

        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.LoginPath = "/Home/Login";
    options.AccessDeniedPath = "/Home/Privacy";
    options.Cookie.Name = "OmegleClone.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
})
.AddCookie("External", options =>
{
    options.Cookie.Name = "OmegleClone.External";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? "")),
        RoleClaimType = ClaimTypes.Role
    };
})
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "";
    options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
    options.SignInScheme = "External";
})
.AddFacebook(options =>
{
    options.AppId = builder.Configuration["Authentication:Facebook:AppId"] ?? "";
    options.AppSecret = builder.Configuration["Authentication:Facebook:AppSecret"] ?? "";
    options.SignInScheme = "External";
});

builder.Services.AddAuthorization();

// ✅ SignalR – hardened
builder.Services.AddSignalR(hub =>
{
    hub.MaximumReceiveMessageSize = 64 * 1024;          // 64 KB max payload (blocks huge SDP/ICE exploits)
    hub.ClientTimeoutInterval     = TimeSpan.FromSeconds(60);
    hub.KeepAliveInterval         = TimeSpan.FromSeconds(20);
    hub.HandshakeTimeout          = TimeSpan.FromSeconds(15);
    hub.EnableDetailedErrors      = builder.Environment.IsDevelopment(); // no stack traces in production
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// ✅ Rate limiting (HTTP endpoints only; WebSocket/SignalR throttled in hub)
builder.Services.AddRateLimiter(opts =>
{
    // General page requests: 120 req/min per IP
    opts.AddFixedWindowLimiter("general", o =>
    {
        o.PermitLimit        = 120;
        o.Window             = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit         = 5;
    });

    // Auth endpoints (login/register): 10 req/min per IP – brute-force protection
    opts.AddFixedWindowLimiter("auth", o =>
    {
        o.PermitLimit        = 10;
        o.Window             = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit         = 0;
    });

    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// MVC
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();

// HostedService
builder.Services.AddHostedService<PremiumExpiryHostedService>();

// Swagger (Dev)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "OmegleCloneMVC", Version = "v1" });

    var jwtSecurityScheme = new OpenApiSecurityScheme
    {
        BearerFormat = "JWT",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Unesi JWT token ovde. Format: Bearer {token}",
        Reference = new OpenApiReference
        {
            Id = JwtBearerDefaults.AuthenticationScheme,
            Type = ReferenceType.SecurityScheme
        }
    };

    c.AddSecurityDefinition(jwtSecurityScheme.Reference.Id, jwtSecurityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { jwtSecurityScheme, Array.Empty<string>() }
    });
});

var app = builder.Build();

// Migracije + seed
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<OmegleCloneMVCContext>();
        context.Database.Migrate();
        DbInitializer.Initialize(context);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred seeding the database.");
    }
}

// Pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// HTTPS redirection samo lokalno – Render terminira TLS na load balanceru,
// pa app prima plain HTTP. UseHttpsRedirection ovdje bi izazvao redirect petlju.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .RequireRateLimiting("general");

// SignalR hubs – no HTTP rate limiter (WebSocket; throttled inside hub)
app.MapHub<ChatHub>("/chatHub");
app.MapHub<TextChatHub>("/textChatHub");

app.Run();
