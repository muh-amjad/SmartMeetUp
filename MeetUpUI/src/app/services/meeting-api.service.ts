import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ChatMessageDto } from '../dtos/meetings/chat-message.dto';
import { CreateMeetingResponseDto } from '../dtos/meetings/create-meeting-response.dto';
import { JoinMeetingResponseDto } from '../dtos/meetings/join-meeting-response.dto';
import { MeetingDetailDto } from '../dtos/meetings/meeting-detail.dto';
import { MeetingListItemDto } from '../dtos/meetings/meeting-list-item.dto';
import { UpdateMeetingRequestDto } from '../dtos/meetings/update-meeting-request.dto';

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

  /** Paginated list of the caller's meetings (host or participant). */
  list(status?: string, page = 1, pageSize = 20): Observable<MeetingListItemDto[]> {
    let params = new HttpParams()
      .set('page', page)
      .set('pageSize', pageSize);

    if (status) {
      params = params.set('status', status);
    }

    return this.http.get<MeetingListItemDto[]>(this.baseUrl, { params });
  }

  /** Full meeting detail with participants. */
  getById(meetingId: string): Observable<MeetingDetailDto> {
    return this.http.get<MeetingDetailDto>(`${this.baseUrl}/${meetingId}`);
  }

  /** Host-only: update title / schedule. */
  update(meetingId: string, payload: UpdateMeetingRequestDto): Observable<void> {
    return this.http.patch<void>(`${this.baseUrl}/${meetingId}`, payload);
  }

  /** Host-only: delete a meeting. */
  remove(meetingId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/${meetingId}`);
  }

  /** Chat message history for a meeting. */
  getChat(meetingId: string): Observable<ChatMessageDto[]> {
    return this.http.get<ChatMessageDto[]>(`${this.baseUrl}/${meetingId}/chat`);
  }
}