import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, map, of, tap, throwError } from 'rxjs';
import { AuthResponseDto } from '../dtos/auth-response.dto';
import { LoginRequestDto } from '../dtos/login-request.dto';
import { RefreshRequestDto } from '../dtos/refresh-request.dto';
import { SignupRequestDto } from '../dtos/signup-request.dto';
import { AuthUser } from '../models/auth-user.model';

const API_BASE_URL = 'https://localhost:7248/api';
const AUTH_TOKEN_KEY = 'meetup.auth.token';
const AUTH_REFRESH_TOKEN_KEY = 'meetup.auth.refresh-token';
const AUTH_USER_KEY = 'meetup.auth.user';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly tokenState = signal<string | null>(localStorage.getItem(AUTH_TOKEN_KEY));
  private readonly refreshTokenState = signal<string | null>(localStorage.getItem(AUTH_REFRESH_TOKEN_KEY));
  private readonly userState = signal<AuthUser | null>(this.readStoredUser());
  private refreshTimerHandle?: ReturnType<typeof setTimeout>;

  readonly token = computed(() => this.tokenState());
  readonly refreshToken = computed(() => this.refreshTokenState());
  readonly currentUser = computed(() => this.userState());
  readonly isAuthenticated = computed(() => !!this.tokenState() && !!this.userState());

  constructor() {
    this.scheduleRefresh();
  }

  signup(payload: SignupRequestDto) {
    return this.http.post<AuthResponseDto>(`${API_BASE_URL}/auth/signup`, payload);
  }

  login(payload: LoginRequestDto) {
    return this.http.post<AuthResponseDto>(`${API_BASE_URL}/auth/login`, payload).pipe(
      tap((response) => this.applyAuthResponse(response)),
    );
  }

  logout() {
    const refreshToken = this.refreshTokenState();
    if (refreshToken) {
      const payload: RefreshRequestDto = { refreshToken };
      this.http.post(`${API_BASE_URL}/auth/logout`, payload).subscribe({
        error: () => {
          // Ignore logout API errors and clear local auth state anyway.
        },
      });
    }

    this.clearAuthState();
    this.router.navigate(['/']);
  }

  refreshAccessToken(): Observable<string> {
    const refreshToken = this.refreshTokenState();
    if (!refreshToken) {
      this.clearAuthState();
      return throwError(() => new Error('No refresh token available.'));
    }

    const payload: RefreshRequestDto = { refreshToken };
    return this.http.post<AuthResponseDto>(`${API_BASE_URL}/auth/refresh`, payload).pipe(
      tap((response) => this.applyAuthResponse(response)),
      map((response) => response.token),
      catchError((error) => {
        this.clearAuthState();
        this.router.navigate(['/login']);
        return throwError(() => error);
      }),
    );
  }

  private clearAuthState() {
    if (this.refreshTimerHandle) {
      clearTimeout(this.refreshTimerHandle);
      this.refreshTimerHandle = undefined;
    }

    this.tokenState.set(null);
    this.refreshTokenState.set(null);
    this.userState.set(null);
    localStorage.removeItem(AUTH_TOKEN_KEY);
    localStorage.removeItem(AUTH_REFRESH_TOKEN_KEY);
    localStorage.removeItem(AUTH_USER_KEY);
  }

  private applyAuthResponse(response: AuthResponseDto) {
    const user: AuthUser = {
      userId: response.userId,
      username: response.username,
      email: response.email,
    };

    this.tokenState.set(response.token);
    this.refreshTokenState.set(response.refreshToken);
    this.userState.set(user);
    localStorage.setItem(AUTH_TOKEN_KEY, response.token);
    localStorage.setItem(AUTH_REFRESH_TOKEN_KEY, response.refreshToken);
    localStorage.setItem(AUTH_USER_KEY, JSON.stringify(user));
    this.scheduleRefresh(response.expiresAtUtc);
  }

  private scheduleRefresh(expiresAtUtc?: string) {
    if (this.refreshTimerHandle) {
      clearTimeout(this.refreshTimerHandle);
      this.refreshTimerHandle = undefined;
    }

    if (!this.tokenState() || !this.refreshTokenState()) {
      return;
    }

    const targetExpiry = expiresAtUtc ? Date.parse(expiresAtUtc) : this.readJwtExpiry(this.tokenState()!);
    if (!targetExpiry || Number.isNaN(targetExpiry)) {
      return;
    }

    const renewInMs = Math.max(5_000, targetExpiry - Date.now() - 60_000);
    this.refreshTimerHandle = setTimeout(() => {
      this.refreshAccessToken().pipe(catchError(() => of(''))).subscribe();
    }, renewInMs);
  }

  private readJwtExpiry(token: string): number | null {
    try {
      const payloadPart = token.split('.')[1];
      const payloadJson = atob(payloadPart.replace(/-/g, '+').replace(/_/g, '/'));
      const payload = JSON.parse(payloadJson) as { exp?: number };
      return payload.exp ? payload.exp * 1000 : null;
    } catch {
      return null;
    }
  }

  private readStoredUser(): AuthUser | null {
    const stored = localStorage.getItem(AUTH_USER_KEY);
    if (!stored) {
      return null;
    }

    try {
      return JSON.parse(stored) as AuthUser;
    } catch {
      return null;
    }
  }
}
