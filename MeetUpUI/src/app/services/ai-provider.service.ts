import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AnalysisProviderDto, UserPreferencesDto } from '../dtos/meetings/analysis.dto';

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
}
