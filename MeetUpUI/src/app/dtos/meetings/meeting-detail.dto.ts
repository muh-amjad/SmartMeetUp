export interface ParticipantDto {
  userId: string;
  username: string;
  role: string;                 // "Host" | "Participant"
  joinedUtc: string | null;
  leftUtc: string | null;
  speakingSeconds: number;
}

export interface MeetingDetailDto {
  meetingId: string;
  title: string;
  hostUserId: string;
  hostUsername: string;
  status: string;
  scheduledStartUtc: string | null;
  actualStartUtc: string | null;
  endedUtc: string | null;
  liveKitRoomName: string;
  recordingDurationSeconds: number | null;
  createdUtc: string;
  isHost: boolean;
  participants: ParticipantDto[];
}