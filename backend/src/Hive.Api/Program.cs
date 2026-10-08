var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness: the process is up. Readiness gets real DB and broker checks in build step 4.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
