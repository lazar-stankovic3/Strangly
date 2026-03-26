using Microsoft.EntityFrameworkCore;
using OmegleCloneMVC.Models;

namespace OmegleCloneMVC.Data
{
    public class OmegleCloneMVCContext : DbContext
    {
        public OmegleCloneMVCContext(DbContextOptions<OmegleCloneMVCContext> options)
            : base(options)
        {
        }

        public DbSet<User> User { get; set; } = default!;
        public DbSet<Role> Role { get; set; } = default!;
        public DbSet<AdminActionLog> AdminActionLogs { get; set; } = default!; // <-- DODAJ

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Mail)
                .IsUnique();

            var roles = Enum.GetValues(typeof(Roles))
                .Cast<Roles>()
                .Select(e => new Role
                {
                    Id = (int)e,
                    Name = e.ToString()
                });

            modelBuilder.Entity<Role>().HasData(roles);

            modelBuilder.Entity<User>()
                .HasOne(u => u.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(u => u.RoleId);

            modelBuilder.Entity<AdminActionLog>()
                .HasIndex(x => x.CreatedUtc);
        }
    }
}
