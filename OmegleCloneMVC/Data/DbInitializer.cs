using Microsoft.AspNetCore.Identity;
using OmegleCloneMVC.Models;

namespace OmegleCloneMVC.Data
{
    public static class DbInitializer
    {
        public static void Initialize(OmegleCloneMVCContext context)
        {
            Console.WriteLine(">>> DbInitializer started <<<");

            // Ako već ima korisnika, ne seeduj ponovo
            if (context.User.Any())
            {
                Console.WriteLine(">>> Users already exist. Skipping seeding.");
                return;
            }

            var hasher = new PasswordHasher<User>();
            var users = new List<User>();

            for (int i = 1; i <= 20; i++)
            {
                var u = new User
                {
                    Username = $"user{i}",
                    Mail = $"user{i}@mail.com",
                    RoleId = (int)Roles.User,
                    IsActive = 1
                };

                u.Password = hasher.HashPassword(u, "pass");
                users.Add(u);
            }

            var admin = new User
            {
                Username = "admin",
                Mail = "admin@mail.com",
                RoleId = (int)Roles.Admin,
                IsActive = 1
            };

            admin.Password = hasher.HashPassword(admin, "admin");
            users.Add(admin);

            context.User.AddRange(users);
            context.SaveChanges();

            Console.WriteLine(">>> DbInitializer finished <<<");
        }
    }
}
