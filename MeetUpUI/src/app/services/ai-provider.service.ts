import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AccountAnalyticsDto,
  ActionItemFilter,
  ActionItemWithMeetingDto,
  AnalysisProviderDto,
  ChangePasswordRequestDto,
  ProfileDto,
  UpdateProfileRequestDto,
  UserPreferencesDto,
} from '../dtos/meetings/analysis.dto';

@Injectable({ providedIn: 'root' })
export class AiProviderService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = environment.apiBaseUrl;

  /** Only providers whose API key is configured on the server. */
  getProviders(): Observable<AnalysisProviderDto[]> {
    return this.http.get<AnalysisProviderDto[]>(`${this.baseUrl}/api/ai/providers`);
  }

  getPreferences(): Observable<UserPreferencesDto> {
    return this.http.get<UserPreferencesDto>(`${this.baseUrl}/api/me/preferences`);
  }

  savePreferences(payload: UserPreferencesDto): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/api/me/preferences`, payload);
  }

  /** Account-wide meeting analytics (server defaults to the last 90 days). */
  getAccountAnalytics(): Observable<AccountAnalyticsDto> {
    return this.http.get<AccountAnalyticsDto>(`${this.baseUrl}/api/me/analytics`);
  }

  /** Action items across every meeting the caller took part in. */
  getActionItems(filter: ActionItemFilter = 'all'): Observable<ActionItemWithMeetingDto[]> {
    return this.http.get<ActionItemWithMeetingDto[]>(
      `${this.baseUrl}/api/me/action-items`,
      { params: { filter } },
    );
  }

  getProfile(): Observable<ProfileDto> {
    return this.http.get<ProfileDto>(`${this.baseUrl}/api/me/profile`);
  }

  updateProfile(payload: UpdateProfileRequestDto): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/api/me/profile`, payload);
  }

  changePassword(payload: ChangePasswordRequestDto): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/api/me/password`, payload);
  }
}
