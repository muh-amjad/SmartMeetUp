using MeetUp.Api.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<Meeting> Meetings => Set<Meeting>();

        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.HasPostgresExtension("vector");

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

            builder.Entity<Meeting>(entity =>
            {
                entity.HasKey(m => m.Id);
            
                entity.Property(m => m.HostUserId)
                    .IsRequired();
            
                entity.Property(m => m.Title)
                    .HasMaxLength(200)
                    .IsRequired();
            
                entity.Property(m => m.LiveKitRoomName)
                    .HasMaxLength(100)
                    .IsRequired();
            
                // Room name must be unique — LiveKit uses it as the room identifier
                entity.HasIndex(m => m.LiveKitRoomName)
                    .IsUnique();
            
                // Fast lookup: "all meetings hosted by user X, ordered by date"
                entity.HasIndex(m => new { m.HostUserId, m.CreatedUtc });
            
                entity.HasOne(m => m.Host)
                    .WithMany()
                    .HasForeignKey(m => m.HostUserId)
                    .OnDelete(DeleteBehavior.Restrict);  // host delete ho jaye toh meetings preserve rahengi
            });
        }
    }
}
