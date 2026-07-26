using MeetUp.Api.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser>
    {
        public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
        public DbSet<Meeting> Meetings => Set<Meeting>();
        public DbSet<MeetingParticipant> MeetingParticipants => Set<MeetingParticipant>();   // ← naya
        public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
        public DbSet<Transcript> Transcripts => Set<Transcript>();
        public DbSet<TranscriptUtterance> TranscriptUtterances => Set<TranscriptUtterance>();

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

                        builder.Entity<MeetingParticipant>(entity =>
            {
                entity.HasKey(p => p.Id);

                entity.Property(p => p.UserId).IsRequired();

                // Ek user ek meeting mein sirf ek baar (unique index)
                entity.HasIndex(p => new { p.MeetingId, p.UserId }).IsUnique();

                // Meeting → Participants cascade delete
                entity.HasOne(p => p.Meeting)
                    .WithMany(m => m.Participants)      // reverse navigation on Meeting
                    .HasForeignKey(p => p.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(p => p.User)
                    .WithMany()
                    .HasForeignKey(p => p.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<ChatMessage>(entity =>
            {
                entity.HasKey(m => m.Id);

                entity.Property(m => m.Text)
                    .HasMaxLength(2000)
                    .IsRequired();

                // Fast retrieval by meeting timeline
                entity.HasIndex(m => new { m.MeetingId, m.SentUtc });

                entity.HasOne(m => m.Meeting)
                    .WithMany(meeting => meeting.ChatMessages)
                    .HasForeignKey(m => m.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(m => m.Sender)
                    .WithMany()
                    .HasForeignKey(m => m.SenderUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<Transcript>(entity =>
            {
                entity.HasKey(t => t.Id);

                entity.Property(t => t.Language)
                    .HasMaxLength(10)
                    .IsRequired();

                // One meeting has at most one transcript (re-transcription overwrites, not appends)
                entity.HasIndex(t => t.MeetingId)
                    .IsUnique();

                entity.HasOne(t => t.Meeting)
                    .WithMany()
                    .HasForeignKey(t => t.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<TranscriptUtterance>(entity =>
            {
                entity.HasKey(u => u.Id);

                entity.Property(u => u.SpeakerLabel)
                    .HasMaxLength(10)
                    .IsRequired();

                // Ordered playback/seek lookups within a transcript
                entity.HasIndex(u => new { u.TranscriptId, u.StartMs });

                entity.HasOne(u => u.Transcript)
                    .WithMany(t => t.Utterances)
                    .HasForeignKey(u => u.TranscriptId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(u => u.Participant)
                    .WithMany()
                    .HasForeignKey(u => u.ParticipantUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
