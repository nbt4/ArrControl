using ArrControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<ArrControlDbContext>();
builder.Services.AddDbContext<ArrControlDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Database")));

var app = builder.Build();
app.UseExceptionHandler();
app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapGet("/api/v1/system/status", () => Results.Ok(new { name = "ArrControl", status = "ok", version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev", utc = DateTimeOffset.UtcNow })).AllowAnonymous();
app.MapGet("/api/v1/instances", async (ArrControlDbContext db, CancellationToken ct) => Results.Ok(await db.Instances.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Kind, x.BaseUrl, x.Enabled }).ToListAsync(ct)));
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
