export type SearchMode = 'keyword' | 'semantic' | 'hybrid';

export interface SearchResultDto {
  meetingId: string;
  meetingTitle: string;
  meetingDate: string;
  snippet: string;
  startMs: number;
  score: number;
}
