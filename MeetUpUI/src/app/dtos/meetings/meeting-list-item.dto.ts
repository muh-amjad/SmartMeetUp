export interface MeetingListItemDto {
  meetingId: string;
  title: string;
  status: string;               // "Scheduled" | "Live" | "Ended" | "Processing" | "Ready" | "Failed"
  scheduledStartUtc: string | null;
  actualStartUtc: string | null;
  endedUtc: string | null;
  createdUtc: string;
  participantCount: number;
  isHost: boolean;
}