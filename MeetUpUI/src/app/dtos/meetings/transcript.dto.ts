export interface TranscriptUtteranceDto {
  speakerLabel: string;
  participantUserId: string | null;
  participantUsername: string | null;
  startMs: number;
  endMs: number;
  text: string;
  confidence: number;
}

export interface TranscriptDto {
  language: string;
  utterances: TranscriptUtteranceDto[];
}

export interface RecordingUrlDto {
  url: string;
  expiresUtc: string;
}
