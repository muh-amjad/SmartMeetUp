import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ActionItemDto,
  DecisionDto,
  FollowUpEmailDto,
  MeetingAnalyticsDto,
  MeetingSummaryDto,
  UpdateActionItemRequestDto,
  UpdateFollowUpEmailRequestDto,
} from '../dtos/meetings/analysis.dto';
import { ChatMessageDto } from '../dtos/meetings/chat-message.dto';
import { CreateMeetingResponseDto } from '../dtos/meetings/create-meeting-response.dto';
import { JoinMeetingResponseDto } from '../dtos/meetings/join-meeting-response.dto';
import { MeetingDetailDto } from '../dtos/meetings/meeting-detail.dto';
import { MeetingListItemDto } from '../dtos/meetings/meeting-list-item.dto';
import { RecordingUrlDto, TranscriptDto } from '../dtos/meetings/transcript.dto';
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

  /** Diarized transcript for a meeting (available once transcription completes). */
  getTranscript(meetingId: string): Observable<TranscriptDto> {
    return this.http.get<TranscriptDto>(`${this.baseUrl}/${meetingId}/transcript`);
  }

  /** Short-lived signed URL for playing back the recording. */
  getRecordingUrl(meetingId: string): Observable<RecordingUrlDto> {
    return this.http.get<RecordingUrlDto>(`${this.baseUrl}/${meetingId}/recording-url`);
  }

  /** Host-only: re-run transcription after a failure. */
  retryTranscript(meetingId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${meetingId}/transcript/retry`, {});
  }

  /** AI-generated overview and key topics. */
  getSummary(meetingId: string): Observable<MeetingSummaryDto> {
    return this.http.get<MeetingSummaryDto>(`${this.baseUrl}/${meetingId}/summary`);
  }

  getActionItems(meetingId: string): Observable<ActionItemDto[]> {
    return this.http.get<ActionItemDto[]>(`${this.baseUrl}/${meetingId}/action-items`);
  }

  getDecisions(meetingId: string): Observable<DecisionDto[]> {
    return this.http.get<DecisionDto[]>(`${this.baseUrl}/${meetingId}/decisions`);
  }

  getFollowUpEmail(meetingId: string): Observable<FollowUpEmailDto> {
    return this.http.get<FollowUpEmailDto>(`${this.baseUrl}/${meetingId}/follow-up-email`);
  }

  /** Host-only: save edits to the drafted follow-up email. */
  updateFollowUpEmail(
    meetingId: string,
    payload: UpdateFollowUpEmailRequestDto,
  ): Observable<void> {
    return this.http.put<void>(`${this.baseUrl}/${meetingId}/follow-up-email`, payload);
  }

  /** Host-only: re-run AI analysis over an existing transcript. */
  retryAnalysis(meetingId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${meetingId}/analysis/retry`, {});
  }

  /** Speaking distribution and word counts for a meeting. */
  getMeetingAnalytics(meetingId: string): Observable<MeetingAnalyticsDto> {
    return this.http.get<MeetingAnalyticsDto>(`${this.baseUrl}/${meetingId}/analytics`);
  }

  /** Host-only: recompute speaker attribution and analytics. */
  recomputeAnalytics(meetingId: string): Observable<void> {
    return this.http.post<void>(`${this.baseUrl}/${meetingId}/analytics/recompute`, {});
  }

  /** Update an action item (status toggle, edit, reassign, due date). */
  updateActionItem(
    actionItemId: string,
    payload: UpdateActionItemRequestDto,
  ): Observable<ActionItemDto> {
    return this.http.patch<ActionItemDto>(
      `${environment.apiBaseUrl}/api/action-items/${actionItemId}`,
      payload,
    );
  }
}