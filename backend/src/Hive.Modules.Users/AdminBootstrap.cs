using System.Security.Cryptography;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Modules.Users;

/// <summary>
/// First start with an empty users table: creates the roles and the admin (spec 13.4 "one-time admin password").
/// The password comes from Auth:BootstrapAdminPassword or is generated and written to the log once.
/// </summary>
public sealed class AdminBootstrap(IServiceScopeFactory scopes, IOptions<AuthOptions> options, ILogger<AdminBootstrap> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<HiveUser>>();

        foreach (var role in HiveRoles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<int>(role));

        if (users.Users.Any())
            return;

        var login = options.Value.BootstrapAdminLogin;
        var password = options.Value.BootstrapAdminPassword;
        var generated = string.IsNullOrEmpty(password);
        if (generated)
            password = GeneratePassword();

        var admin = new HiveUser { UserName = login, DisplayName = "Администратор", CreatedAt = DateTimeOffset.UtcNow };
        var created = await users.CreateAsync(admin, password!);
        if (!created.Succeeded)
            throw new InvalidOperationException("Cannot create admin: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(admin, HiveRoles.Admin);

        if (generated)
            logger.LogWarning("Created user '{Login}' with one-time password: {Password} (change it after the first login)", login, password);
        else
            logger.LogInformation("Created user '{Login}' with the configured bootstrap password", login);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Join('-', Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetString(alphabet, 5)));
    }
}
