using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services.Ai;
using Pgvector;

namespace MeetUp.Api.Jobs;

/// <summary>
/// Splits a finished transcript into searchable chunks and embeds them.
///
/// Chunks are written even when no embedding key is configured: the keyword arm of search runs off
/// the generated tsvector on each chunk, so text search works without any AI credentials — only the
/// semantic arm needs them.
/// </summary>
public sealed class EmbeddingJob : IEmbeddingJob
{
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly ISearchRepository _searchRepository;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<EmbeddingJob> _logger;

    public EmbeddingJob(
        ITranscriptRepository transcriptRepository,
        ISearchRepository searchRepository,
        IEmbeddingService embeddingService,
        ILogger<EmbeddingJob> logger)
    {
        _transcriptRepository = transcriptRepository;
        _searchRepository = searchRepository;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task RunAsync(Guid meetingId, CancellationToken ct)
    {
        var transcript = await _transcriptRepository.GetByMeetingIdAsync(meetingId, ct);
        if (transcript is null || transcript.Utterances.Count == 0)
        {
            _logger.LogWarning("EmbeddingJob: meeting {MeetingId} has no transcript to index", meetingId);
            return;
        }

        var drafts = TranscriptChunker.Chunk(transcript.Utterances.ToList());
        if (drafts.Count == 0)
        {
            return;
        }

        var chunks = drafts
            .Select(d => new TranscriptChunk
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                TranscriptId = transcript.Id,
                Text = d.Text,
                StartMs = d.StartMs,
                EndMs = d.EndMs,
            })
            .ToList();

        if (_embeddingService.IsConfigured)
        {
            try
            {
                var vectors = await _embeddingService.EmbedBatchAsync(
                    chunks.Select(c => c.Text).ToList(), ct);

                for (var i = 0; i < chunks.Count; i++)
                {
                    chunks[i].Embedding = new Vector(vectors[i]);
                }
            }
            catch (Exception ex)
            {
                // Keep the chunks so keyword search still works; semantic search just misses this
                // meeting until the job is run again.
                _logger.LogError(ex,
                    "EmbeddingJob: embedding failed for meeting {MeetingId}; storing chunks without vectors",
                    meetingId);
            }
        }
        else
        {
            _logger.LogInformation(
                "EmbeddingJob: no Gemini API key configured — meeting {MeetingId} is indexed for keyword search only",
                meetingId);
        }

        // Re-running replaces the previous index rather than duplicating it.
        await _searchRepository.ReplaceChunksAsync(meetingId, chunks, ct);
        await _searchRepository.SaveChangesAsync(ct);

        var embedded = chunks.Count(c => c.Embedding is not null);
        _logger.LogInformation(
            "Meeting {MeetingId} indexed: {ChunkCount} chunk(s), {Embedded} with embeddings",
            meetingId, chunks.Count, embedded);
    }
}
