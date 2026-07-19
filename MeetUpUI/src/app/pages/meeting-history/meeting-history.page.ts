import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MeetingListItemDto } from '../../dtos/meetings/meeting-list-item.dto';
import { MeetingApiService } from '../../services/meeting-api.service';

@Component({
  selector: 'app-meeting-history',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './meeting-history.page.html',
  styleUrl: './meeting-history.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetingHistoryPage implements OnInit {
  private readonly meetingApi = inject(MeetingApiService);
  private readonly router = inject(Router);

  readonly meetings = signal<MeetingListItemDto[]>([]);
  readonly loading = signal(false);
  readonly errorMessage = signal('');

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.errorMessage.set('');

    this.meetingApi.list().subscribe({
      next: (list) => {
        this.meetings.set(list);
        this.loading.set(false);
      },
      error: (err) => {
        this.errorMessage.set('Could not load meetings.');
        this.loading.set(false);
        console.error('meeting list failed', err);
      },
    });
  }

  openMeeting(meeting: MeetingListItemDto): void {
    // If meeting is Live, join it. Otherwise show detail (Phase 3+ mein detail page banayenge).
    if (meeting.status === 'Live' || meeting.status === 'Scheduled') {
      this.router.navigate(['/meet', meeting.meetingId]);
    } else {
      // For now, just alert. Later: navigate to a detail page.
      window.alert(`Meeting "${meeting.title}" — Status: ${meeting.status}`);
    }
  }

  backToDashboard(): void {
    this.router.navigate(['/dashboard']);
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
}