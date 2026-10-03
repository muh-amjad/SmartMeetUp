import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MeetingListItemDto } from '../../dtos/meetings/meeting-list-item.dto';
import { isMeetingProcessing, ProcessingRecheckMs } from '../../models/meeting-status';
import { MeetingApiService } from '../../services/meeting-api.service';

@Component({
  selector: 'app-meeting-history',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './meeting-history.page.html',
  styleUrl: './meeting-history.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetingHistoryPage implements OnInit, OnDestroy {
  private readonly meetingApi = inject(MeetingApiService);
  private readonly router = inject(Router);

  readonly meetings = signal<MeetingListItemDto[]>([]);
  readonly loading = signal(false);
  readonly errorMessage = signal('');

  readonly isProcessing = isMeetingProcessing;

  private recheckTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    void this.load();
  }

  ngOnDestroy(): void {
    this.clearRecheck();
  }

  /** `silent` refreshes in place, without the loading state, for the background re-check. */
  async load(silent = false): Promise<void> {
    this.clearRecheck();
    if (!silent) {
      this.loading.set(true);
      this.errorMessage.set('');
    }

    this.meetingApi.list().subscribe({
      next: (list) => {
        this.meetings.set(list);
        this.loading.set(false);
        this.scheduleRecheckIfProcessing(list);
      },
      error: (err) => {
        if (!silent) {
          this.errorMessage.set('Could not load meetings.');
        }
        this.loading.set(false);
        console.error('meeting list failed', err);
      },
    });
  }

  openMeeting(meeting: MeetingListItemDto): void {
    // Still being processed: its page would only show empty panels. The button is disabled too;
    // this guards anything else that calls in.
    if (isMeetingProcessing(meeting.status)) {
      return;
    }

    // Live/Scheduled → rejoin the call; anything ended → open the detail page (transcript, recording).
    if (meeting.status === 'Live' || meeting.status === 'Scheduled') {
      this.router.navigate(['/meet', meeting.meetingId]);
    } else {
      this.router.navigate(['/meetings', meeting.meetingId]);
    }
  }

  statusClass(status: string): string {
    switch (status) {
      case 'Live':       return 'status-live';
      case 'Ready':      return 'status-ready';
      case 'Processing': return 'status-processing';
      case 'Failed':     return 'status-failed';
      default:           return 'status-default';
    }
  }

  /** While anything is processing, re-check so it becomes openable without a manual reload. */
  private scheduleRecheckIfProcessing(list: MeetingListItemDto[]): void {
    if (list.some((m) => isMeetingProcessing(m.status))) {
      this.recheckTimer = setTimeout(() => void this.load(true), ProcessingRecheckMs);
    }
  }

  private clearRecheck(): void {
    if (this.recheckTimer !== null) {
      clearTimeout(this.recheckTimer);
      this.recheckTimer = null;
    }
  }
}
