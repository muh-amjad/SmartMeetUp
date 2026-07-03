export interface UserSearchResultDto {
  userId: string;
  username: string;
  email: string;
  isOnline: boolean;
  connectionId?: string | null;
}
