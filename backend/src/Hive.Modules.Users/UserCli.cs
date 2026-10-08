using System.Security.Cryptography;
using Hive.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Modules.Users;

/// <summary>
/// `Hive.Api --add-user &lt;login&gt; &lt;admin|member|viewer&gt;`: creates a user with a one-time password
/// (until the users screen exists, spec 8.2 "Система").
/// </summary>
public static class UserCli
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var i = Array.IndexOf(args, "--add-user");
        if (i < 0 || args.Length < i + 3 || !HiveRoles.All.Contains(args[i + 2]))
        {
            Console.Error.WriteLine($"Usage: Hive.Api --add-user <login> <{string.Join('|', HiveRoles.All)}>");
            return 2;
        }
        var (login, role) = (args[i + 1], args[i + 2]);

        using var scope = services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<HiveUser>>();
        if (!await roles.RoleExistsAsync(role))
            await roles.CreateAsync(new IdentityRole<int>(role));

        var password = GeneratePassword();
        var user = new HiveUser { UserName = login, CreatedAt = DateTimeOffset.UtcNow };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("Cannot create user: " + string.Join("; ", result.Errors.Select(e => e.Description)));
            return 1;
        }
        await users.AddToRoleAsync(user, role);
        Console.WriteLine($"Created '{login}' ({role}). One-time password: {password}");
        return 0;
    }

    internal static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Join('-', Enumerable.Range(0, 3).Select(_ => RandomNumberGenerator.GetString(alphabet, 5)));
    }
}
