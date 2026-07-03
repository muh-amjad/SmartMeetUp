export interface AuthResponseDto {
  token: string;
  refreshToken: string;
  expiresAtUtc: string;
  userId: string;
  username: string;
  email: string;
}
