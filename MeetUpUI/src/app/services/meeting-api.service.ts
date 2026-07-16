import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateMeetingResponseDto } from '../dtos/meetings/create-meeting-response.dto';
import { JoinMeetingResponseDto } from '../dtos/meetings/join-meeting-response.dto';

/**
 * HTTP wrapper for /api/meetings endpoints. Deals purely with REST — no LiveKit.
 * Injected by LivekitMeetingService when it needs a fresh access token.
 */
@Injectable({ providedIn: 'root' })
export class MeetingApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/api/meetings`;

  /** Create a new meeting; host receives their own token in the response. */
  create(title?: string): Observable<CreateMeetingResponseDto> {
    return this.http.post<CreateMeetingResponseDto>(this.baseUrl, { title });
  }

  /** Get a token to join an existing meeting. */
  join(meetingId: string): Observable<JoinMeetingResponseDto> {
    return this.http.post<JoinMeetingResponseDto>(`${this.baseUrl}/${meetingId}/join`, {});
  }

  /** Host-only: close the LiveKit room. */
  end(meetingId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${meetingId}/end`, {});
  }
}