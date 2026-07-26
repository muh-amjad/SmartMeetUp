import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  OnInit,
  signal,
  ViewChild,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MeetingDetailDto } from '../../dtos/meetings/meeting-detail.dto';
import { TranscriptUtteranceDto } from '../../dtos/meetings/transcript.dto';
import { MeetingApiService } from '../../services/meeting-api.service';
import { ToastService } from '../../services/toast.service';
import { TranscriptViewerComponent } from '../../components/transcript-viewer/transcript-viewer.component';

@Component({
  selector: 'app-meeting-detail',
  standalone: true,
  imports: [DatePipe, TranscriptViewerComponent],
  templateUrl: './meeting-detail.page.html',
  styleUrl: './meeting-detail.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetingDetailPage implements OnInit {
  private readonly meetingApi = inject(MeetingApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  @ViewChild('audioPlayer')
  audioPlayerRef?: ElementRef<HTMLAudioElement>;

  readonly meeting = signal<MeetingDetailDto | null>(null);
  readonly utterances = signal<TranscriptUtteranceDto[]>([]);
  readonly recordingUrl = signal<string | null>(null);
  readonly loading = signal(true);
  readonly transcriptState = signal<'loading' | 'ready' | 'unavailable'>('loading');
  readonly retrying = signal(false);

  private meetingId = '';

  async ngOnInit(): Promise<void> {
    this.meetingId = this.route.snapshot.paramMap.get('meetingId') ?? '';
    if (!this.meetingId) {
      this.router.navigate(['/meetings']);
      return;
    }

    await this.loadMeeting();
    await Promise.all([this.loadTranscript(), this.loadRecording()]);
  }

  private async loadMeeting(): Promise<void> {
    this.loading.set(true);
    this.meetingApi.getById(this.meetingId).subscribe({
      next: (m) => {
        this.meeting.set(m);
        this.loading.set(false);
      },
      error: () => {
        this.toast.error('Could not load meeting.');
        this.loading.set(false);
        this.router.navigate(['/meetings']);
      },
    });
  }

  private loadTranscript(): Promise<void> {
    return new Promise((resolve) => {
      this.transcriptState.set('loading');
      this.meetingApi.getTranscript(this.meetingId).subscribe({
        next: (t) => {
          this.utterances.set(t.utterances);
          this.transcriptState.set('ready');
          resolve();
        },
        // 404 = not transcribed yet (or failed); treat as "unavailable" rather than an error toast.
        error: () => {
          this.transcriptState.set('unavailable');
          resolve();
        },
      });
    });
  }

  private loadRecording(): Promise<void> {
    return new Promise((resolve) => {
      this.meetingApi.getRecordingUrl(this.meetingId).subscribe({
        next: (r) => {
          this.recordingUrl.set(r.url);
          resolve();
        },
        error: () => resolve(),
      });
    });
  }

  async retryTranscript(): Promise<void> {
    if (this.retrying()) {
      return;
    }
    this.retrying.set(true);
    this.meetingApi.retryTranscript(this.meetingId).subscribe({
      next: () => {
        this.toast.info('Transcription re-queued. Check back in a few minutes.');
        this.retrying.set(false);
      },
      error: () => {
        this.toast.error('Could not retry transcription.');
        this.retrying.set(false);
      },
    });
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
