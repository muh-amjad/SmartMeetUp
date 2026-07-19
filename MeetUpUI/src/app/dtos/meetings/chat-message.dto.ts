export interface ChatMessageDto {
  id: string;
  senderUserId: string;
  senderUsername: string;
  text: string;
  sentUtc: string;
}