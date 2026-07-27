using MeetUp.Api.Entities;

namespace MeetUp.Api.Services.Ai;

public sealed record ChunkDraft(string Text, int StartMs, int EndMs);

/// <summary>
/// Groups consecutive utterances into search-sized chunks.
///
/// The plan chunks the concatenated FullText by token count, but that text carries no timestamps —
/// chunking from utterances instead keeps real start/end times, which is what lets a search result
/// jump to the moment in the recording. Utterance boundaries are also natural sentence boundaries,
/// so chunks never split mid-word.
/// </summary>
public static class TranscriptChunker
{
    /// <summary>~300 tokens at roughly 4 characters per token.</summary>
    public const int TargetChars = 1200;

    /// <summary>~50 tokens of overlap, so a phrase spanning a boundary is still findable.</summary>
    public const int OverlapChars = 200;

    public static IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<TranscriptUtterance> utterances)
    {
        var ordered = utterances
            .Where(u => !string.IsNullOrWhiteSpace(u.Text))
            .OrderBy(u => u.StartMs)
            .ToList();

        if (ordered.Count == 0)
        {
            return Array.Empty<ChunkDraft>();
        }

        var chunks = new List<ChunkDraft>();
        var current = new List<TranscriptUtterance>();
        var currentLength = 0;

        foreach (var utterance in ordered)
        {
            current.Add(utterance);
            currentLength += utterance.Text.Length + 1;

            if (currentLength < TargetChars)
            {
                continue;
            }

            chunks.Add(Build(current));

            // Carry the tail of this chunk into the next one so a phrase straddling the boundary
            // still appears whole somewhere.
            current = TakeOverlap(current);
            currentLength = current.Sum(u => u.Text.Length + 1);
        }

        // Whatever is left is a final chunk, unless the overlap already covers all of it.
        if (current.Count > 0)
        {
            var draft = Build(current);
            if (chunks.Count == 0 || chunks[^1].EndMs < draft.EndMs)
            {
                chunks.Add(draft);
            }
        }

        return chunks;
    }

    private static ChunkDraft Build(List<TranscriptUtterance> utterances) => new(
        string.Join(' ', utterances.Select(u => u.Text.Trim())),
        utterances.Min(u => u.StartMs),
        utterances.Max(u => u.EndMs));

    /// <summary>Keeps trailing utterances up to the overlap budget, always at least one.</summary>
    private static List<TranscriptUtterance> TakeOverlap(List<TranscriptUtterance> utterances)
    {
        var overlap = new List<TranscriptUtterance>();
        var length = 0;

        for (var i = utterances.Count - 1; i >= 0; i--)
        {
            var next = length + utterances[i].Text.Length + 1;
            if (overlap.Count > 0 && next > OverlapChars)
            {
                break;
            }

            overlap.Insert(0, utterances[i]);
            length = next;
        }

        // A single utterance longer than the whole target would otherwise repeat forever.
        return length >= TargetChars ? new List<TranscriptUtterance>() : overlap;
    }
}
