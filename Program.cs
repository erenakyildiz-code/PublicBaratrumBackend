using Baratrum.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Baratrum.Services;
using Baratrum.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter your valid token in the text input below.\r\n\r\nExample: 'eyJhbGci...'"
    });

    // Use a delegate here to satisfy the OpenApiDocument requirRheement
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", document),
            new List<string>() 
        }
    });
});

// Configure the MSSQL Database connection
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));


builder.Services.AddIdentityApiEndpoints<ApplicationUser>(options => {
    options.SignIn.RequireConfirmedEmail = !METADATA.RUNNINGLOCAL;
})

.AddEntityFrameworkStores<AppDbContext>()
.AddUserManager<CustomUserManager>();

builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

builder.Services.AddTransient<IEmailSender, EmailSender>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("BaratrumPolicy", policy =>
    {
        if (builder.Environment.IsDevelopment() || METADATA.RUNNINGLOCAL)
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
        else
        {
            // Strict policy for production
            policy.WithOrigins("https://baratrum.com", "https://www.baratrum.com","localapp://localhost")
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

builder.Services.AddHttpClient();
builder.Services.AddControllers();

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("BaratrumPolicy");
// Add Authentication and Authorization middleware
app.UseWebSockets();
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (request.Path.StartsWithSegments("/ws") && request.Query.TryGetValue("access_token", out var token))
    {
        request.Headers.Authorization = $"Bearer {token}";
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapIdentityApi<ApplicationUser>()
   .AddEndpointFilter(async (context, next) => 
   {
       if ((context.HttpContext.Request.Path.Equals("/register", StringComparison.OrdinalIgnoreCase) || 
            context.HttpContext.Request.Path.Equals("/confirmEmail", StringComparison.OrdinalIgnoreCase)) && 
            !METADATA.RUNNINGLOCAL )
       {
           return Results.NotFound("");
       }
       return await next(context);
   });



app.MapControllers();
app.Run();