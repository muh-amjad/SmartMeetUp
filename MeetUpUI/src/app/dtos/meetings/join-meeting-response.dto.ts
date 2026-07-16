export interface JoinMeetingResponseDto {
  meetingId: string;
  title: string;
  livekitToken: string;
  livekitWsUrl: string;
  roomName: string;
  isHost: boolean;
}