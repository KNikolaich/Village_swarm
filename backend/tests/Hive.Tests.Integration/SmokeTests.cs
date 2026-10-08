namespace Hive.Tests.Integration;

// Testcontainers-based tests (PostgreSQL, Mosquitto) arrive with ingest in build step 4.
public class SmokeTests
{
    [Fact]
    public void Api_assembly_loads() => Assert.NotNull(typeof(Program).Assembly);
}
