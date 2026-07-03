using MeetUp.Api.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<ApplicationUser>(entity =>
            {
                entity.Property(user => user.DisplayName)
                    .HasMaxLength(120)
                    .IsRequired();
            });

            builder.Entity<RefreshToken>(entity =>
            {
                entity.HasKey(token => token.Id);
                entity.Property(token => token.Token)
                    .HasMaxLength(200)
                    .IsRequired();
                entity.HasIndex(token => token.Token)
                    .IsUnique();
                entity.Property(token => token.UserId)
                    .IsRequired();
                entity.HasOne(token => token.User)
                    .WithMany()
                    .HasForeignKey(token => token.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
