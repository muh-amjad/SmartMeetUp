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
        public DbSet<TranscriptChunk> TranscriptChunks => Set<TranscriptChunk>();
        public DbSet<MeetingSummary> MeetingSummaries => Set<MeetingSummary>();
        public DbSet<ActionItem> ActionItems => Set<ActionItem>();
        public DbSet<Decision> Decisions => Set<Decision>();
        public DbSet<FollowUpEmail> FollowUpEmails => Set<FollowUpEmail>();
        public DbSet<FollowUpEmailRecipient> FollowUpEmailRecipients => Set<FollowUpEmailRecipient>();
        public DbSet<ParticipantAudioActivity> ParticipantAudioActivities => Set<ParticipantAudioActivity>();
        public DbSet<MeetingAnalytics> MeetingAnalytics => Set<MeetingAnalytics>();

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

            builder.Entity<TranscriptChunk>(entity =>
            {
                entity.HasKey(c => c.Id);

                entity.Property(c => c.Text).IsRequired();

                entity.Property(c => c.Embedding)
                    .HasColumnType("vector(768)");

                // HNSW rather than the planned ivfflat: ivfflat needs lists tuned to row count
                // (~rows/1000), and at demo scale lists=100 would leave ~0 vectors per list while
                // the default probes=1 searches a single list — the index would silently miss
                // almost every match. HNSW needs no such tuning and keeps recall high when small.
                entity.HasIndex(c => c.Embedding)
                    .HasMethod("hnsw")
                    .HasOperators("vector_cosine_ops");

                // Postgres maintains this; EF must never try to write it.
                entity.Property(c => c.SearchVector)
                    .HasColumnType("tsvector")
                    .HasComputedColumnSql("to_tsvector('english', \"Text\")", stored: true);

                entity.HasIndex(c => c.SearchVector)
                    .HasMethod("GIN");

                // Search always filters by the caller's meetings first, so lead with MeetingId.
                entity.HasIndex(c => new { c.MeetingId, c.StartMs });

                entity.HasOne(c => c.Meeting)
                    .WithMany()
                    .HasForeignKey(c => c.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(c => c.Transcript)
                    .WithMany()
                    .HasForeignKey(c => c.TranscriptId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<MeetingSummary>(entity =>
            {
                entity.HasKey(s => s.Id);

                // One summary per meeting — re-analysis replaces it rather than stacking up
                entity.HasIndex(s => s.MeetingId)
                    .IsUnique();

                entity.Property(s => s.KeyTopics)
                    .HasColumnType("jsonb")
                    .HasConversion(JsonbConverter.For<string>(), JsonbConverter.ComparerFor<string>());

                entity.Property(s => s.ProviderKey)
                    .HasMaxLength(60)
                    .IsRequired();

                entity.Property(s => s.ModelUsed)
                    .HasMaxLength(120)
                    .IsRequired();

                entity.HasOne(s => s.Meeting)
                    .WithMany()
                    .HasForeignKey(s => s.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<ActionItem>(entity =>
            {
                entity.HasKey(a => a.Id);

                entity.Property(a => a.Description)
                    .HasMaxLength(1000)
                    .IsRequired();

                entity.HasIndex(a => new { a.MeetingId, a.Status });

                // "My open action items across all meetings" (dashboard widget in Phase 8)
                entity.HasIndex(a => new { a.AssigneeUserId, a.Status });

                entity.HasOne(a => a.Meeting)
                    .WithMany()
                    .HasForeignKey(a => a.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(a => a.Assignee)
                    .WithMany()
                    .HasForeignKey(a => a.AssigneeUserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasOne(a => a.SourceUtterance)
                    .WithMany()
                    .HasForeignKey(a => a.SourceUtteranceId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<Decision>(entity =>
            {
                entity.HasKey(d => d.Id);

                entity.Property(d => d.Description)
                    .HasMaxLength(1000)
                    .IsRequired();

                entity.HasIndex(d => d.MeetingId);

                entity.HasOne(d => d.Meeting)
                    .WithMany()
                    .HasForeignKey(d => d.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(d => d.SourceUtterance)
                    .WithMany()
                    .HasForeignKey(d => d.SourceUtteranceId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<FollowUpEmail>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Subject)
                    .HasMaxLength(300)
                    .IsRequired();

                entity.HasIndex(e => e.MeetingId)
                    .IsUnique();

                entity.HasOne(e => e.Meeting)
                    .WithMany()
                    .HasForeignKey(e => e.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.EditedBy)
                    .WithMany()
                    .HasForeignKey(e => e.EditedByUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<FollowUpEmailRecipient>(entity =>
            {
                entity.HasKey(r => r.Id);

                entity.Property(r => r.RecipientEmail)
                    .HasMaxLength(256)
                    .IsRequired();

                entity.HasIndex(r => r.FollowUpEmailId);

                entity.HasOne(r => r.FollowUpEmail)
                    .WithMany()
                    .HasForeignKey(r => r.FollowUpEmailId)
                    .OnDelete(DeleteBehavior.Cascade);

                // The audit row outlives the account: clearing the link keeps the address on record.
                entity.HasOne(r => r.RecipientUser)
                    .WithMany()
                    .HasForeignKey(r => r.RecipientUserId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<ParticipantAudioActivity>(entity =>
            {
                entity.HasKey(a => a.Id);

                entity.Property(a => a.UserId).IsRequired();

                // Speaker mapping scans every interval for a meeting, ordered by time
                entity.HasIndex(a => new { a.MeetingId, a.StartedSpeakingMs });

                entity.HasOne(a => a.Meeting)
                    .WithMany()
                    .HasForeignKey(a => a.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(a => a.User)
                    .WithMany()
                    .HasForeignKey(a => a.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            builder.Entity<MeetingAnalytics>(entity =>
            {
                // MeetingId doubles as the primary key — analytics is a 1:1 extension of a meeting
                entity.HasKey(a => a.MeetingId);

                entity.Property(a => a.SpeakingDistribution)
                    .HasColumnType("jsonb")
                    .HasConversion(
                        JsonbConverter.For<SpeakingShare>(),
                        JsonbConverter.ComparerFor<SpeakingShare>());

                entity.HasOne(a => a.Meeting)
                    .WithOne()
                    .HasForeignKey<MeetingAnalytics>(a => a.MeetingId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
