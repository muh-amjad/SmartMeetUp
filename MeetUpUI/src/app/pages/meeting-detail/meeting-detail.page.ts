import { DatePipe, DecimalPipe } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  OnInit,
  signal,
  ViewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import {
  ActionItemDto,
  DecisionDto,
  FollowUpEmailDto,
  MeetingAnalyticsDto,
  MeetingSummaryDto,
} from '../../dtos/meetings/analysis.dto';
import { MeetingDetailDto } from '../../dtos/meetings/meeting-detail.dto';
import { TranscriptUtteranceDto } from '../../dtos/meetings/transcript.dto';
import { MeetingApiService } from '../../services/meeting-api.service';
import { ToastService } from '../../services/toast.service';
import { TranscriptViewerComponent } from '../../components/transcript-viewer/transcript-viewer.component';

type DetailTab = 'overview' | 'transcript' | 'actions' | 'decisions' | 'email' | 'analytics';
type LoadState = 'loading' | 'ready' | 'unavailable';

@Component({
  selector: 'app-meeting-detail',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, TranscriptViewerComponent],
  templateUrl: './meeting-detail.page.html',
  styleUrl: './meeting-detail.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetingDetailPage implements OnInit, AfterViewInit {
  private readonly meetingApi = inject(MeetingApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  @ViewChild('audioPlayer')
  audioPlayerRef?: ElementRef<HTMLAudioElement>;

  readonly tabs: ReadonlyArray<{ id: DetailTab; label: string }> = [
    { id: 'overview', label: 'Overview' },
    { id: 'transcript', label: 'Transcript' },
    { id: 'actions', label: 'Action Items' },
    { id: 'decisions', label: 'Decisions' },
    { id: 'email', label: 'Follow-up Email' },
    { id: 'analytics', label: 'Analytics' },
  ];

  readonly activeTab = signal<DetailTab>('overview');

  readonly meeting = signal<MeetingDetailDto | null>(null);
  readonly recordingUrl = signal<string | null>(null);
  readonly loading = signal(true);

  readonly utterances = signal<TranscriptUtteranceDto[]>([]);
  readonly transcriptState = signal<LoadState>('loading');

  readonly summary = signal<MeetingSummaryDto | null>(null);
  readonly summaryState = signal<LoadState>('loading');

  readonly actionItems = signal<ActionItemDto[]>([]);
  readonly decisions = signal<DecisionDto[]>([]);

  readonly email = signal<FollowUpEmailDto | null>(null);
  readonly emailState = signal<LoadState>('loading');
  readonly emailEditing = signal(false);
  readonly emailDraftSubject = signal('');
  readonly emailDraftBody = signal('');

  readonly analytics = signal<MeetingAnalyticsDto | null>(null);
  readonly analyticsState = signal<LoadState>('loading');

  readonly retryingTranscript = signal(false);
  readonly retryingAnalysis = signal(false);
  readonly recomputing = signal(false);
  readonly savingEmail = signal(false);

  private meetingId = '';

  async ngOnInit(): Promise<void> {
    this.meetingId = this.route.snapshot.paramMap.get('meetingId') ?? '';
    if (!this.meetingId) {
      this.router.navigate(['/meetings']);
      return;
    }

    // Arriving from a search result: open the requested tab so the moment is visible.
    const requestedTab = this.route.snapshot.queryParamMap.get('tab');
    if (requestedTab && this.tabs.some((t) => t.id === requestedTab)) {
      this.activeTab.set(requestedTab as DetailTab);
    }

    await this.loadMeeting();
    await Promise.all([
      this.loadTranscript(),
      this.loadRecording(),
      this.loadAnalysis(),
      this.loadAnalytics(),
    ]);

    // Seek only once the audio element and transcript exist, so the position sticks.
    const seekMs = Number(this.route.snapshot.queryParamMap.get('seek'));
    if (Number.isFinite(seekMs) && seekMs > 0) {
      this.pendingSeekSeconds = seekMs / 1000;
      this.applyPendingSeek();
    }
  }

  private pendingSeekSeconds: number | null = null;

  ngAfterViewInit(): void {
    this.applyPendingSeek();
  }

  /**
   * The audio element only exists once a recording URL has resolved, which can land either side of
   * the view being initialised — so try from both places and clear the request once it lands.
   */
  private applyPendingSeek(): void {
    if (this.pendingSeekSeconds === null) {
      return;
    }

    const audio = this.audioPlayerRef?.nativeElement;
    if (!audio) {
      return;
    }

    audio.currentTime = this.pendingSeekSeconds;
    this.pendingSeekSeconds = null;
  }

  private async loadAnalytics(): Promise<void> {
    this.analyticsState.set('loading');
    try {
      this.analytics.set(await firstValueFrom(this.meetingApi.getMeetingAnalytics(this.meetingId)));
      this.analyticsState.set('ready');
    } catch {
      // 404 until the speaker-mapping job has run for this meeting.
      this.analyticsState.set('unavailable');
    }
  }

  async recomputeAnalytics(): Promise<void> {
    if (this.recomputing()) {
      return;
    }
    this.recomputing.set(true);
    try {
      await firstValueFrom(this.meetingApi.recomputeAnalytics(this.meetingId));
      this.toast.info('Analytics re-queued. Check back in a moment.');
    } catch {
      this.toast.error('Could not recompute analytics. A transcript is required first.');
    } finally {
      this.recomputing.set(false);
    }
  }

  /** Bar width as a percentage of the longest bar, so the widest always fills the plot. */
  barWidthPercent(seconds: number): number {
    const max = Math.max(...(this.analytics()?.speakingDistribution ?? []).map((s) => s.seconds), 0);
    return max > 0 ? Math.round((seconds / max) * 100) : 0;
  }

  formatDuration(totalSeconds: number): string {
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
  }

  selectTab(tab: DetailTab): void {
    this.activeTab.set(tab);
  }

  private async loadMeeting(): Promise<void> {
    this.loading.set(true);
    try {
      this.meeting.set(await firstValueFrom(this.meetingApi.getById(this.meetingId)));
    } catch {
      this.toast.error('Could not load meeting.');
      this.router.navigate(['/meetings']);
    } finally {
      this.loading.set(false);
    }
  }

  private async loadTranscript(): Promise<void> {
    this.transcriptState.set('loading');
    try {
      const transcript = await firstValueFrom(this.meetingApi.getTranscript(this.meetingId));
      this.utterances.set(transcript.utterances);
      this.transcriptState.set('ready');
    } catch {
      // 404 just means "not transcribed yet" — not an error worth a toast.
      this.transcriptState.set('unavailable');
    }
  }

  private async loadRecording(): Promise<void> {
    try {
      const recording = await firstValueFrom(this.meetingApi.getRecordingUrl(this.meetingId));
      this.recordingUrl.set(recording.url);
    } catch {
      this.recordingUrl.set(null);
    }
  }

  /** Summary/email 404 until analysis has run; the list endpoints just come back empty. */
  private async loadAnalysis(): Promise<void> {
    this.summaryState.set('loading');
    this.emailState.set('loading');

    const summary = firstValueFrom(this.meetingApi.getSummary(this.meetingId))
      .then((s) => {
        this.summary.set(s);
        this.summaryState.set('ready');
      })
      .catch(() => this.summaryState.set('unavailable'));

    const email = firstValueFrom(this.meetingApi.getFollowUpEmail(this.meetingId))
      .then((e) => {
        this.email.set(e);
        this.emailDraftSubject.set(e.subject);
        this.emailDraftBody.set(e.bodyMarkdown);
        this.emailState.set('ready');
      })
      .catch(() => this.emailState.set('unavailable'));

    const items = firstValueFrom(this.meetingApi.getActionItems(this.meetingId))
      .then((list) => this.actionItems.set(list))
      .catch(() => this.actionItems.set([]));

    const decisions = firstValueFrom(this.meetingApi.getDecisions(this.meetingId))
      .then((list) => this.decisions.set(list))
      .catch(() => this.decisions.set([]));

    await Promise.all([summary, email, items, decisions]);
  }

  async toggleActionItem(item: ActionItemDto): Promise<void> {
    const nextStatus = item.status === 'Done' ? 'Open' : 'Done';
    try {
      const updated = await firstValueFrom(
        this.meetingApi.updateActionItem(item.id, { status: nextStatus }),
      );
      this.actionItems.update((items) => items.map((i) => (i.id === updated.id ? updated : i)));
    } catch {
      this.toast.error('Could not update the action item.');
    }
  }

  isOverdue(item: ActionItemDto): boolean {
    return (
      item.status !== 'Done' &&
      item.dueDateUtc !== null &&
      new Date(item.dueDateUtc).getTime() < Date.now()
    );
  }

  assigneeLabel(item: ActionItemDto): string {
    return item.assigneeUsername ?? item.assigneeNameRaw ?? 'Unassigned';
  }

  async retryTranscript(): Promise<void> {
    if (this.retryingTranscript()) {
      return;
    }
    this.retryingTranscript.set(true);
    try {
      await firstValueFrom(this.meetingApi.retryTranscript(this.meetingId));
      this.toast.info('Transcription re-queued. Check back in a few minutes.');
    } catch {
      this.toast.error('Could not retry transcription.');
    } finally {
      this.retryingTranscript.set(false);
    }
  }

  async retryAnalysis(): Promise<void> {
    if (this.retryingAnalysis()) {
      return;
    }
    this.retryingAnalysis.set(true);
    try {
      await firstValueFrom(this.meetingApi.retryAnalysis(this.meetingId));
      this.toast.info('AI analysis re-queued. Check back in a minute.');
    } catch {
      this.toast.error('Could not retry analysis. A transcript is required first.');
    } finally {
      this.retryingAnalysis.set(false);
    }
  }

  startEditingEmail(): void {
    const current = this.email();
    if (current) {
      this.emailDraftSubject.set(current.subject);
      this.emailDraftBody.set(current.bodyMarkdown);
    }
    this.emailEditing.set(true);
  }

  cancelEditingEmail(): void {
    const current = this.email();
    if (current) {
      this.emailDraftSubject.set(current.subject);
      this.emailDraftBody.set(current.bodyMarkdown);
    }
    this.emailEditing.set(false);
  }

  async saveEmail(): Promise<void> {
    if (this.savingEmail()) {
      return;
    }
    this.savingEmail.set(true);
    const subject = this.emailDraftSubject();
    const bodyMarkdown = this.emailDraftBody();

    try {
      await firstValueFrom(
        this.meetingApi.updateFollowUpEmail(this.meetingId, { subject, bodyMarkdown }),
      );
      this.email.update((e) => (e ? { ...e, subject, bodyMarkdown } : e));
      this.emailEditing.set(false);
      this.toast.success('Follow-up email saved.');
    } catch {
      this.toast.error('Could not save the follow-up email.');
    } finally {
      this.savingEmail.set(false);
    }
  }

  seekAudio(seconds: number): void {
    const audio = this.audioPlayerRef?.nativeElement;
    if (audio) {
      audio.currentTime = seconds;
      void audio.play();
    }
  }

  back(): void {
    this.router.navigate(['/meetings']);
  }
}
