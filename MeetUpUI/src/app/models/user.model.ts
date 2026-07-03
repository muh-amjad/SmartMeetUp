export interface User {
  id: string;
  appUserId?: string;
  username: string;
  email?: string;
  isInCall: boolean;
  roomId?: string | null;
}
