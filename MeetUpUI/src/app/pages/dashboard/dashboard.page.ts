import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { FormBuilder, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AccountAnalyticsDto, ActionItemWithMeetingDto } from '../../dtos/meetings/analysis.dto';
import { isMeetingProcessing, ProcessingRecheckMs } from '../../models/meeting-status';
import { MeetingListItemDto } from '../../dtos/meetings/meeting-list-item.dto';
import { UserSearchResultDto } from '../../dtos/user-search-result.dto';
import { AiProviderService } from '../../services/ai-provider.service';
import { AuthService } from '../../services/auth.service';
import { MeetingApiService } from '../../services/meeting-api.service';
import { SignalrService } from '../../services/signalr.service';
import { UserDirectoryService } from '../../services/user-directory.service';

/**
 * The signed-in landing page: what happened recently, what is outstanding, and the two ways to
 * start a call. The call room itself lives in MeetupHome — this page only creates the meeting and
 * sends the invite, then hands off by navigating.
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [DatePipe, DecimalPipe, FormsModule, ReactiveFormsModule, RouterLink],
  templateUrl: './dashboard.page.html',
  styleUrl: './dashboard.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardPage implements OnInit, OnDestroy {
  private readonly meetingApi = inject(MeetingApiService);
  private readonly aiProviders = inject(AiProviderService);
  private readonly userDirectory = inject(UserDirectoryService);
  private readonly signalR = inject(SignalrService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);

  readonly searchForm: FormGroup = this.fb.group({
    query: ['', [Validators.required, Validators.minLength(2)]],
  });

  readonly displayName = signal('');

  readonly recentMeetings = signal<MeetingListItemDto[]>([]);
  readonly isProcessing = isMeetingProcessing;
  private recheckTimer: ReturnType<typeof setTimeout> | null = null;
  readonly openItems = signal<ActionItemWithMeetingDto[]>([]);
  readonly analytics = signal<AccountAnalyticsDto | null>(null);
  readonly loading = signal(true);

  readonly searchResults = signal<UserSearchResultDto[]>([]);
  readonly searching = signal(false);
  readonly searchMessage = signal('Search for a teammate to call them directly.');

  readonly incomingInvite = signal<{
    inviteId: string;
    meetingId: string;
    fromUsername: string;
  } | null>(null);
  readonly ringingMessage = signal('');

  async ngOnInit(): Promise<void> {
    this.displayName.set(this.auth.currentUser()?.username ?? '');

    // Presence + invites are wired here so a call can still reach you while you sit on the
    // dashboard. Only one routed page is alive at a time, so this never fights the call room's
    // own handlers over the hub's callback registry.
    this.bindCallbacks();
    this.signalR.attachSignalRHandlers();
    await this.signalR.connectAndJoin();

    await this.loadSummary();
  }

  ngOnDestroy(): void {
    this.signalR.setCallbacks({});
    this.clearRecheck();
  }

  private async loadSummary(): Promise<void> {
    this.loading.set(true);

    const meetings = firstValueFrom(this.meetingApi.list(undefined, 1, 5))
      .then((list) => {
        this.recentMeetings.set(list);
        this.scheduleRecheckIfProcessing(list);
      })
      .catch(() => this.recentMeetings.set([]));

    const items = firstValueFrom(this.aiProviders.getActionItems('open'))
      .then((list) => this.openItems.set(list.slice(0, 5)))
      .catch(() => this.openItems.set([]));

    const stats = firstValueFrom(this.aiProviders.getAccountAnalytics())
      .then((a) => this.analytics.set(a))
      .catch(() => this.analytics.set(null));

    await Promise.all([meetings, items, stats]);
    this.loading.set(false);
  }

  /** Meetings held in the current Monday-start week, from the weekly buckets. */
  thisWeek(): { meetings: number; minutes: number } {
    const weeks = this.analytics()?.weeklyBreakdown ?? [];
    if (!weeks.length) {
      return { meetings: 0, minutes: 0 };
    }
    const latest = weeks[weeks.length - 1];
    return { meetings: latest.meetingCount, minutes: latest.totalMinutes };
  }

  startInstantMeeting(): void {
    this.router.navigate(['/preview']);
  }

  async searchUsers(): Promise<void> {
    if (this.searchForm.invalid || this.searching()) {
      return;
    }

    const query = this.searchForm.getRawValue().query as string;
    this.searching.set(true);
    this.searchMessage.set('Searching…');

    try {
      const results = await firstValueFrom(this.userDirectory.searchUsers(query));
      this.searchResults.set(results);
      this.searchMessage.set(
        results.length ? `Found ${results.length} teammate(s).` : 'Nobody matched that search.',
      );
    } catch {
      this.searchResults.set([]);
      this.searchMessage.set('Search failed. Please try again.');
    } finally {
      this.searching.set(false);
    }
  }

  /** Creates the meeting, rings the target, then moves into the room to wait for them. */
  async call(result: UserSearchResultDto): Promise<void> {
    if (!result.isOnline || !result.connectionId) {
      return;
    }

    try {
      this.ringingMessage.set(`Ringing ${result.username}…`);
      const meeting = await firstValueFrom(this.meetingApi.create());
      await this.signalR.inviteToMeeting(result.connectionId, meeting.meetingId);
      await this.router.navigate(['/meet', meeting.meetingId]);
    } catch {
      this.ringingMessage.set('');
      this.searchMessage.set('Could not start the call.');
    }
  }

  async acceptIncoming(): Promise<void> {
    const invite = this.incomingInvite();
    if (!invite) {
      return;
    }
    this.incomingInvite.set(null);
    await this.signalR.respondToInvite(invite.inviteId, true);
    await this.router.navigate(['/meet', invite.meetingId]);
  }

  async declineIncoming(): Promise<void> {
    const invite = this.incomingInvite();
    if (!invite) {
      return;
    }
    this.incomingInvite.set(null);
    await this.signalR.respondToInvite(invite.inviteId, false);
  }

  statusClass(status: string): string {
    switch (status) {
      case 'Live': return 'status-live';
      case 'Ready': return 'status-ready';
      case 'Processing': return 'status-processing';
      case 'Failed': return 'status-failed';
      default: return 'status-default';
    }
  }

  openMeeting(meeting: MeetingListItemDto): void {
    // Still being processed: its page would only show empty panels. The row is disabled too.
    if (isMeetingProcessing(meeting.status)) {
      return;
    }

    if (meeting.status === 'Live' || meeting.status === 'Scheduled') {
      this.router.navigate(['/meet', meeting.meetingId]);
    } else {
      this.router.navigate(['/meetings', meeting.meetingId]);
    }
  }

  /** While a recent meeting is processing, re-check it so it becomes openable on its own. */
  private scheduleRecheckIfProcessing(list: MeetingListItemDto[]): void {
    this.clearRecheck();
    if (!list.some((m) => isMeetingProcessing(m.status))) {
      return;
    }
    this.recheckTimer = setTimeout(() => {
      firstValueFrom(this.meetingApi.list(undefined, 1, 5))
        .then((fresh) => {
          this.recentMeetings.set(fresh);
          this.scheduleRecheckIfProcessing(fresh);
        })
        .catch(() => this.scheduleRecheckIfProcessing(this.recentMeetings()));
    }, ProcessingRecheckMs);
  }

  private clearRecheck(): void {
    if (this.recheckTimer !== null) {
      clearTimeout(this.recheckTimer);
      this.recheckTimer = null;
    }
  }

  private bindCallbacks(): void {
    this.signalR.setCallbacks({
      onIncomingInvite: (payload) =>
        this.incomingInvite.set({
          inviteId: payload.inviteId,
          meetingId: payload.meetingId,
          fromUsername: payload.fromUsername,
        }),
      onInviteDeclined: (payload) => {
        this.ringingMessage.set('');
        this.searchMessage.set(`${payload.declinedByUsername} declined the call.`);
      },
      onCallFailed: (message) => {
        this.ringingMessage.set('');
        this.searchMessage.set(message);
      },
    });
  }
}
