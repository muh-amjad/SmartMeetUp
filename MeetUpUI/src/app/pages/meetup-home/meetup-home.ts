import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  OnDestroy,
  OnInit,
  signal,
  ViewChild,
} from '@angular/core';
import { FormBuilder, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { gsap } from 'gsap';
import { firstValueFrom } from 'rxjs';
import { UserSearchResultDto } from '../../dtos/user-search-result.dto';
import { UserDto } from '../../dtos/user.dto';
import { User } from '../../models/user.model';
import { AuthService } from '../../services/auth.service';
import { LivekitMeetingService } from '../../services/livekit-meeting.service';
import { MeetingApiService } from '../../services/meeting-api.service';
import { SignalrService } from '../../services/signalr.service';
import { UserDirectoryService } from '../../services/user-directory.service';
import { UsersFacade } from '../../store/facades/users.facade';
import { ChatMessageDto } from '../../dtos/meetings/chat-message.dto';

@Component({
  selector: 'app-meetup-home',
  standalone: true,
  templateUrl: './meetup-home.html',
  styleUrl: './meetup-home.css',
  imports: [CommonModule, FormsModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetupHome implements OnInit, AfterViewInit, OnDestroy {
  private readonly usersFacade = inject(UsersFacade);
  private readonly signalRService = inject(SignalrService);
  private readonly authService = inject(AuthService);
  private readonly userDirectoryService = inject(UserDirectoryService);
  private readonly livekit = inject(LivekitMeetingService);
  private readonly meetingApi = inject(MeetingApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);

  readonly searchForm: FormGroup = this.fb.group({
    query: ['', [Validators.required, Validators.minLength(2)]],
  });

  readonly dashboardMode = signal<'dashboard' | 'call'>('call');
  readonly mediaError = signal('');

  readonly allUsers = this.usersFacade.users;
  readonly currentUsername = computed(() => this.authService.currentUser()?.username ?? '');
  readonly currentEmail = computed(() => this.authService.currentUser()?.email ?? '');

  readonly currentUserConnectionId = signal('');

  readonly incomingInvite = signal<{
    inviteId: string;
    meetingId: string;
    fromUserId: string;
    fromUsername: string;
  } | null>(null);

  readonly ringingMessage = signal('');
  readonly searchResults = signal<UserSearchResultDto[]>([]);
  readonly searching = signal(false);
  readonly searchMessage = signal(
    'Search by username or email to find someone and call if online.',
  );

  /** Reactive LiveKit state — passed straight through to the template. */
  readonly remoteParticipants = this.livekit.remoteParticipants;
  readonly localParticipant = this.livekit.localParticipant;
  readonly currentMeetingId = this.livekit.currentMeetingId;
  readonly isRecording = this.livekit.isRecording;
    readonly chatMessages = signal<ChatMessageDto[]>([]);
  readonly chatDraft = signal('');
  readonly chatOpen = signal(true);   // toggle chat panel visibility

  readonly onlineUsers = computed(() => {
    const myConnectionId = this.currentUserConnectionId();
    return this.allUsers().filter((u) => u.id !== myConnectionId);
  });

  private isEndingCall = false;

  private remoteStreamCache = new Map<string, { stream: MediaStream; trackId: string }>();

  @ViewChild('pageShell', { static: true })
  pageShellRef!: ElementRef<HTMLElement>;

  @ViewChild('localVideo')
  localVideoRef?: ElementRef<HTMLVideoElement>;

  constructor() {
    // Re-attach local video element whenever LiveKit publishes a new local track.
    effect(() => {
      const lp = this.localParticipant();
      const el = this.localVideoRef?.nativeElement;
      if (lp && el) {
        const track = lp
          .getTrackPublications()
          .find((pub) => pub.kind === 'video' && !pub.isMuted)?.videoTrack;
        if (track) {
          track.attach(el);
        }
      }
    });
  }

  async ngOnInit(): Promise<void> {
    const modeFromRoute = this.route.snapshot.data['mode'];
    this.dashboardMode.set(modeFromRoute === 'dashboard' ? 'dashboard' : 'call');

    this.bindSignalrCallbacks();
    this.signalRService.attachSignalRHandlers();
    await this.signalRService.connectAndJoin();
    this.currentUserConnectionId.set(this.signalRService.connectionId);

    if (this.dashboardMode() === 'call') {
      const meetingIdFromUrl = this.route.snapshot.paramMap.get('meetingId');
      const state = history.state as { source?: string; meetingId?: string } | undefined;
      const meetingId = meetingIdFromUrl ?? state?.meetingId;

      if (meetingId) {
        try {
          await this.livekit.joinMeeting(meetingId);
          await this.signalRService.setInCall(meetingId);
          await this.loadChatHistory(meetingId);
        } catch (err) {
          this.mediaError.set('Could not join meeting. Please try again.');
          console.error('joinMeeting failed', err);
        }
      } else if (state?.source === 'join-now') {
        try {
          const created = await firstValueFrom(this.meetingApi.create());
          // Update URL so user can share/refresh
          await this.router.navigate(['/meet', created.meetingId], { replaceUrl: true });
          await this.livekit.joinMeeting(created.meetingId);
          await this.signalRService.setInCall(created.meetingId);
          await this.loadChatHistory(created.meetingId);
        } catch (err) {
          this.mediaError.set('Could not start meeting.');
          console.error('startInstantMeeting failed', err);
        }
      }
    }
  }

  ngAfterViewInit(): void {
    const shell = this.pageShellRef.nativeElement;
    gsap.from(shell.querySelectorAll('.top-bar, .search-panel, .content-section, .users-section'), {
      opacity: 0,
      y: 22,
      duration: 0.8,
      stagger: 0.1,
      ease: 'power3.out',
    });
  }

  ngOnDestroy(): void {
    if (this.dashboardMode() === 'call' && this.currentMeetingId() && !this.isEndingCall) {
      void this.livekit.leaveMeeting();
      void this.signalRService.setLeftCall();
    }

    this.signalRService.setCallbacks({});
  }

  async searchUsers(): Promise<void> {
    if (this.searchForm.invalid || this.searching()) {
      return;
    }

    const query = this.searchForm.getRawValue().query as string;
    this.searching.set(true);
    this.searchMessage.set('Searching users...');

    this.userDirectoryService.searchUsers(query).subscribe({
      next: (results) => {
        this.searching.set(false);
        this.searchResults.set(results);
        this.searchMessage.set(
          results.length ? `Found ${results.length} user(s).` : 'No users matched your search.',
        );
      },
      error: () => {
        this.searching.set(false);
        this.searchResults.set([]);
        this.searchMessage.set('Search failed. Please try again.');
      },
    });
  }

  callSearchedUser(result: UserSearchResultDto): void {
    if (!result.isOnline || !result.connectionId) {
      window.alert('This user is currently offline.');
      return;
    }

    void this.callUser(
      new UserDto(result.username, result.connectionId, result.userId, result.email),
    );
  }

  /**
   * Start a call to another online user.
   * 1. Create a meeting via REST (get meetingId + token for us).
   * 2. Send an invite via SignalR (server routes it to the target's connection).
   * 3. Join the LiveKit room ourselves so we're already there when they accept.
   */
  async callUser(user: User): Promise<void> {
    try {
      this.ringingMessage.set(`Ringing ${user.username}...`);
      const meeting = await firstValueFrom(this.meetingApi.create());
      await this.signalRService.inviteToMeeting(user.id, meeting.meetingId);

      if (this.dashboardMode() === 'dashboard') {
        // Move to the meeting page so we see our own video while ringing
        await this.router.navigate(['/meet', meeting.meetingId]);
      } else {
        // Already in call mode — join directly
        await this.livekit.joinMeeting(meeting.meetingId);
        await this.signalRService.setInCall(meeting.meetingId);
        await this.loadChatHistory(meeting.meetingId);
      }
    } catch (err) {
      this.ringingMessage.set('');
      this.mediaError.set('Could not start the call.');
      console.error('callUser failed', err);
    }
  }

  async endCall(): Promise<void> {
    this.isEndingCall = true;
    const meetingId = this.currentMeetingId();

    await this.livekit.leaveMeeting();
    await this.signalRService.setLeftCall();
    this.ringingMessage.set('');
    this.incomingInvite.set(null);
    this.chatMessages.set([]);
    this.chatDraft.set('');

    // If the host wants to explicitly close the room for everyone, uncomment:
    // if (meetingId && this.livekit.isHost()) {
    //   try { await firstValueFrom(this.meetingApi.end(meetingId)); } catch { /* ignore */ }
    // }

    if (this.dashboardMode() === 'call') {
      await this.router.navigate(['/dashboard']);
    }
  }

  async logout(): Promise<void> {
    await this.endCall();
    await this.signalRService.disconnect();
    this.authService.logout();
  }

  goHome(): void {
    this.router.navigate(['/']);
  }

  goToHistory(): void {
    this.router.navigate(['/meetings']);
  }

  goToSettings(): void {
    this.router.navigate(['/settings']);
  }

  goToAnalytics(): void {
    this.router.navigate(['/analytics']);
  }

  goToSearch(): void {
    this.router.navigate(['/search']);
  }

  openSupport(): void {
    window.alert('Support chat widget is ready. We can wire behavior in the next step.');
  }

  startInstantMeeting(): void {
    this.router.navigate(['/preview']);
  }

  toggleCamera(): void {
    void this.livekit.toggleCamera();
  }

  toggleMic(): void {
    void this.livekit.toggleMic();
  }

  canCall(user: User): boolean {
    if (!this.currentUserConnectionId()) {
      return false;
    }

    if (user.id === this.currentUserConnectionId()) {
      return false;
    }

    return !user.isInCall;
  }

  userStatus(user: User): string {
    return user.isInCall ? 'In a call' : 'Available';
  }

  acceptIncomingCall(): void {
    const invite = this.incomingInvite();
    if (!invite) {
      return;
    }

    if (this.dashboardMode() === 'dashboard') {
      // Navigate to /meet/:meetingId — ngOnInit there reads the meetingId from
      // the URL and calls joinMeeting() itself. Don't ALSO join here: both
      // calls would race to connect() the same singleton LivekitMeetingService
      // for the same participant, and one connection attempt loses.
      void this.signalRService.respondToInvite(invite.inviteId, true);
      this.router.navigate(['/meet', invite.meetingId]);
      this.incomingInvite.set(null);
      return;
    }

    // In call mode, we can join right here
    void this.signalRService.respondToInvite(invite.inviteId, true);
    void this.livekit.joinMeeting(invite.meetingId).catch((err) => {
      this.mediaError.set('Could not join the call.');
      console.error('acceptIncomingCall failed', err);
    });
    this.incomingInvite.set(null);
  }

  rejectIncomingCall(): void {
    const invite = this.incomingInvite();
    if (!invite) {
      return;
    }

    void this.signalRService.respondToInvite(invite.inviteId, false);
    this.incomingInvite.set(null);
  }

  trackByParticipantId(_: number, item: { identity: string }): string {
    return item.identity;
  }

  get isCameraOn(): boolean {
    return this.livekit.isCameraOn();
  }

  get isMicOn(): boolean {
    return this.livekit.isMicOn();
  }

  /** Returns a stable MediaStream for the participant's video, or null. */
  getRemoteVideoStream(participant: {
    identity: string;
    getTrackPublications: () => any[];
  }): MediaStream | null {
    const videoPub = participant
      .getTrackPublications()
      .find((pub) => pub.kind === 'video' && !pub.isMuted);
    const track = videoPub?.track?.mediaStreamTrack as MediaStreamTrack | undefined;

    if (!track) {
      this.remoteStreamCache.delete(participant.identity);
      return null;
    }

    const cached = this.remoteStreamCache.get(participant.identity);
    if (cached && cached.trackId === track.id) {
      return cached.stream;
    }

    const stream = new MediaStream([track]);
    this.remoteStreamCache.set(participant.identity, { stream, trackId: track.id });
    return stream;
  }

  isRemoteCameraOn(participant: { getTrackPublications: () => any[] }): boolean {
    const pub = participant.getTrackPublications().find((p) => p.kind === 'video');
    return !!pub && !pub.isMuted;
  }

  isRemoteMicOn(participant: { getTrackPublications: () => any[] }): boolean {
    const pub = participant.getTrackPublications().find((p) => p.kind === 'audio');
    return !!pub && !pub.isMuted;
  }

  getParticipantName(participant: { name?: string; identity: string }): string {
    return participant.name || participant.identity;
  }

    /** Load chat history for the given meeting into the signal. */
  private async loadChatHistory(meetingId: string): Promise<void> {
    try {
      const history = await firstValueFrom(this.meetingApi.getChat(meetingId));
      this.chatMessages.set(history);
    } catch (err) {
      console.warn('Could not load chat history', err);
      this.chatMessages.set([]);
    }
  }

  toggleChat(): void {
    this.chatOpen.update((v) => !v);
  }

  async sendChat(): Promise<void> {
    const text = this.chatDraft().trim();
    const meetingId = this.currentMeetingId();
    if (!text || !meetingId) {
      return;
    }

    try {
      await this.signalRService.sendChatMessage(meetingId, text);
      this.chatDraft.set('');
    } catch (err) {
      console.error('sendChat failed', err);
    }
  }

  trackByChatId(_: number, item: ChatMessageDto): string {
    return item.id;
  }

  formatChatTime(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  isMyMessage(msg: ChatMessageDto): boolean {
    // Compare against the current app user id from AuthService if available; fallback:
    // messages sent by us have senderUserId == our AspNetUsers.Id which SignalRService
    // does not expose. Simplest visual cue: compare senderUsername with current username.
    return msg.senderUsername === this.currentUsername();
  }

  private bindSignalrCallbacks(): void {
    this.signalRService.setCallbacks({
      onIncomingInvite: (payload) => {
        this.incomingInvite.set(payload);
      },
      onInviteRinging: () => {
        // Optional: could show "ringing" indicator; currently ringingMessage set in callUser()
      },
      onInviteDeclined: (payload) => {
        this.ringingMessage.set('');
        window.alert(`${payload.declinedByUsername} declined the call.`);
        // Caller was already in the room waiting — leave since no one is coming
        void this.livekit.leaveMeeting();
      },
      onInviteAccepted: (payload) => {
        this.ringingMessage.set('');
        // Both sides call joinMeeting; the callee's Angular calls it in acceptIncomingCall().
        // The caller is already in the room from callUser(), so nothing more to do here.
        // Just log for visibility.
        console.log(
          'Invite accepted by',
          payload.acceptedByUsername,
          'for meeting',
          payload.meetingId,
        );
      },
      onCallFailed: (message) => {
        this.ringingMessage.set('');
        window.alert(message);
      },
      onChatMessageReceived: (payload) => {
        this.chatMessages.update((list) => [
          ...list,
          {
            id: payload.id,
            senderUserId: payload.senderUserId,
            senderUsername: payload.senderUsername,
            text: payload.text,
            sentUtc: payload.sentUtc,
          },
        ]);
      },
    });
  }
}
