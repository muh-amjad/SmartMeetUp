export interface CreateMeetingResponseDto {
  meetingId: string;
  title: string;
  livekitToken: string;
  livekitWsUrl: string;
  roomName: string;
}