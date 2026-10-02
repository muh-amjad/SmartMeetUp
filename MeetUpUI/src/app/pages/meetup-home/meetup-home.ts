import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  OnDestroy,
  OnInit,
  signal,
  untracked,
  viewChild,
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
import { ToastService } from '../../services/toast.service';
import { UserDirectoryService } from '../../services/user-directory.service';
import { UsersFacade } from '../../store/facades/users.facade';
import { ChatMessageDto } from '../../dtos/meetings/chat-message.dto';

/** Where an invite sent from inside a call stands, per invited user. */
type InviteState = 'ringing' | 'declined' | 'failed';

/** The single side panel open next to the video grid — one at a time keeps mobile usable. */
type CallPanel = 'chat' | 'people';

/** How long an unanswered invite shows as ringing before it can be sent again. */
const InviteRingTimeoutMs = 45_000;

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
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);

  readonly searchForm: FormGroup = this.fb.group({
    query: ['', [Validators.required, Validators.minLength(2)]],
  });

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
  readonly currentMeetingTitle = this.livekit.currentMeetingTitle;
  readonly isRecording = this.livekit.isRecording;
  readonly cameraEnabled = this.livekit.cameraEnabled;
  readonly micEnabled = this.livekit.micEnabled;
  readonly activeSpeakerIds = this.livekit.activeSpeakerIds;

  readonly chatMessages = signal<ChatMessageDto[]>([]);
  readonly chatDraft = signal('');

  /** Chat starts open where there is room beside the video; on a phone it would cover it. */
  readonly panel = signal<CallPanel | null>(this.isWideScreen() ? 'chat' : null);

  readonly inCall = computed(() => !!this.currentMeetingId());

  /** Everyone on screen: you plus each remote participant. Drives the grid's shape. */
  readonly tileCount = computed(() => this.remoteParticipants().length + 1);

  readonly onlineUsers = computed(() => {
    const myConnectionId = this.currentUserConnectionId();
    return this.allUsers().filter((u) => u.id !== myConnectionId);
  });

  /** Invites sent from this call, keyed by the invitee's connection id. */
  readonly inviteStates = signal<ReadonlyMap<string, InviteState>>(new Map());
  readonly peopleFilter = signal('');

  /**
   * Online users who could be added to the current call: not you (on any device) and not already
   * in this meeting. Anyone free comes first; people in another call can still be invited and
   * decide for themselves whether to switch.
   */
  readonly addableUsers = computed(() => {
    const meetingId = this.currentMeetingId()?.toLowerCase();
    const myIdentity = this.localParticipant()?.identity;
    const filter = this.peopleFilter().trim().toLowerCase();

    return this.onlineUsers()
      .filter((u) => !myIdentity || u.appUserId !== myIdentity)
      .filter((u) => !meetingId || (u.roomId ?? '').toLowerCase() !== meetingId)
      .filter(
        (u) =>
          !filter ||
          u.username.toLowerCase().includes(filter) ||
          (u.email ?? '').toLowerCase().includes(filter),
      )
      .sort((a, b) => Number(a.isInCall) - Number(b.isInCall) || a.username.localeCompare(b.username));
  });

  private isEndingCall = false;

  /** The meeting this client created for a 1:1 call, so a decline can end it if nobody came. */
  private startedMeetingId: string | null = null;

  /** Who the most recent in-call invite went to; CallFailed does not say. */
  private lastInviteTarget: string | null = null;

  private readonly inviteTimers = new Map<string, ReturnType<typeof setTimeout>>();

  private remoteStreamCache = new Map<string, { stream: MediaStream; trackId: string }>();

  // Audio is cached separately from video on purpose. A participant can turn their camera off while
  // still talking, and if both shared one stream the audio element would lose its source the moment
  // the video track went away.
  private remoteAudioStreamCache = new Map<string, { stream: MediaStream; trackId: string }>();

  @ViewChild('pageShell', { static: true })
  pageShellRef!: ElementRef<HTMLElement>;

  /**
   * A signal rather than @ViewChild: the self-view only exists while in a call, so it appears after
   * the first render. The effect below re-runs when it does — a plain ViewChild would leave the
   * effect looking at `undefined` and the self-view would stay black.
   */
  private readonly localVideoRef = viewChild<ElementRef<HTMLVideoElement>>('localVideo');

  constructor() {
    // Attach the local camera to the self-view whenever either side changes.
    effect(() => {
      const el = this.localVideoRef()?.nativeElement;
      const lp = this.localParticipant();
      this.livekit.localTrackVersion();
      const cameraOn = this.cameraEnabled();

      if (!el || !lp || !cameraOn) {
        return;
      }
      const track = lp
        .getTrackPublications()
        .find((pub) => pub.kind === 'video' && !pub.isMuted)?.videoTrack;
      track?.attach(el);
    });

    // A new meeting starts with a clean slate of invites.
    effect(() => {
      this.currentMeetingId();
      untracked(() => this.clearInviteStates());
    });

    // On a narrow upright screen the panel is a sheet over the video. Close it when the screen
    // becomes one (a phone rotated upright, a window narrowed) so the call is not left hidden.
    if (typeof window !== 'undefined' && window.matchMedia) {
      const narrowPortrait = window.matchMedia('(max-width: 900px) and (orientation: portrait)');
      const onChange = (e: MediaQueryListEvent) => {
        if (e.matches) {
          this.panel.set(null);
        }
      };
      narrowPortrait.addEventListener('change', onChange);
      inject(DestroyRef).onDestroy(() => narrowPortrait.removeEventListener('change', onChange));
    }
  }

  async ngOnInit(): Promise<void> {
    this.bindSignalrCallbacks();
    this.signalRService.attachSignalRHandlers();
    await this.signalRService.connectAndJoin();
    this.currentUserConnectionId.set(this.signalRService.connectionId);

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

  ngAfterViewInit(): void {
    const shell = this.pageShellRef.nativeElement;
    const lobby = shell.querySelectorAll('.top-bar, .search-panel, .users-section');
    if (lobby.length) {
      gsap.from(lobby, {
        opacity: 0,
        y: 22,
        duration: 0.8,
        stagger: 0.1,
        ease: 'power3.out',
      });
    }
  }

  ngOnDestroy(): void {
    if (this.currentMeetingId() && !this.isEndingCall) {
      void this.livekit.leaveMeeting();
      void this.signalRService.setLeftCall();
    }

    this.clearInviteStates();
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
      this.toast.info(`${result.username} is currently offline.`);
      return;
    }

    void this.callUser(
      new UserDto(result.username, result.connectionId, result.userId, result.email),
    );
  }

  /**
   * Calls another online user. Inside a call this adds them to it; otherwise it starts a new one.
   *
   * It used to always create a fresh meeting and join it — so calling a third person from inside a
   * call silently pulled the caller out of the call they were in and into a new one alone with the
   * invitee, leaving the other participant behind.
   */
  async callUser(user: User): Promise<void> {
    if (this.currentMeetingId()) {
      await this.addToCall(user);
      return;
    }

    try {
      this.ringingMessage.set(`Ringing ${user.username}...`);
      const meeting = await firstValueFrom(this.meetingApi.create());
      this.startedMeetingId = meeting.meetingId;
      await this.signalRService.inviteToMeeting(user.id, meeting.meetingId);

      await this.livekit.joinMeeting(meeting.meetingId);
      await this.signalRService.setInCall(meeting.meetingId);
      await this.loadChatHistory(meeting.meetingId);
    } catch (err) {
      this.ringingMessage.set('');
      this.startedMeetingId = null;
      this.mediaError.set('Could not start the call.');
      console.error('callUser failed', err);
    }
  }

  /**
   * Invites an online user into the meeting already in progress. They receive the same invite as
   * for a new call, and accepting joins them to this room — so recording, transcription and
   * analysis cover them like everyone else, with no extra wiring.
   */
  async addToCall(user: User): Promise<void> {
    const meetingId = this.currentMeetingId();
    if (!meetingId || this.inviteStateFor(user) === 'ringing') {
      return;
    }

    this.setInviteState(user.id, 'ringing');
    this.lastInviteTarget = user.id;

    // Free the button again if they never answer, so they can be asked a second time.
    this.clearInviteTimer(user.id);
    this.inviteTimers.set(
      user.id,
      setTimeout(() => {
        if (this.inviteStates().get(user.id) === 'ringing') {
          this.setInviteState(user.id, null);
        }
      }, InviteRingTimeoutMs),
    );

    try {
      await this.signalRService.inviteToMeeting(user.id, meetingId);
    } catch (err) {
      this.setInviteState(user.id, 'failed');
      this.toast.error(`Could not invite ${user.username}.`);
      console.error('addToCall failed', err);
    }
  }

  inviteStateFor(user: User): InviteState | undefined {
    return this.inviteStates().get(user.id);
  }

  async endCall(): Promise<void> {
    this.isEndingCall = true;

    await this.livekit.leaveMeeting();
    await this.signalRService.setLeftCall();
    this.ringingMessage.set('');
    this.incomingInvite.set(null);
    this.chatMessages.set([]);
    this.chatDraft.set('');
    this.startedMeetingId = null;

    await this.router.navigate(['/dashboard']);
  }

  async logout(): Promise<void> {
    await this.endCall();
    await this.signalRService.disconnect();
    this.authService.logout();
  }

  /** The call room sits outside the app shell, so it carries its own way back to the nav. */
  goToDashboard(): void {
    this.router.navigate(['/dashboard']);
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

  /** Opens a side panel, or closes it if it is the one already showing. */
  togglePanel(panel: CallPanel): void {
    this.panel.update((current) => (current === panel ? null : panel));
  }

  closePanel(): void {
    this.panel.set(null);
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

  async acceptIncomingCall(): Promise<void> {
    const invite = this.incomingInvite();
    if (!invite) {
      return;
    }

    // Already in the room, so join right here. (The dashboard takes the other route: it
    // navigates to /meet/:meetingId and lets ngOnInit join, rather than joining twice and
    // racing two connect() calls against the same singleton LivekitMeetingService.)
    // Accepting while in another call switches to the new one: joinMeeting leaves the old room.
    this.incomingInvite.set(null);
    this.startedMeetingId = null;
    void this.signalRService.respondToInvite(invite.inviteId, true);

    try {
      await this.livekit.joinMeeting(invite.meetingId);
      // Every other join path does this too. Without it the hub never learns this connection is
      // in the meeting, so it silently drops this user's chat messages and speaking intervals —
      // and with no intervals, their transcript lines can never be attributed and stay "Speaker B".
      await this.signalRService.setInCall(invite.meetingId);
      await this.loadChatHistory(invite.meetingId);
    } catch (err) {
      this.mediaError.set('Could not join the call.');
      console.error('acceptIncomingCall failed', err);
    }
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

  /** Action labels, kept identical to the button text the e2e suite looks up. */
  get isCameraOn(): boolean {
    return this.cameraEnabled();
  }

  get isMicOn(): boolean {
    return this.micEnabled();
  }

  isSpeaking(identity: string | undefined): boolean {
    return !!identity && this.activeSpeakerIds().has(identity);
  }

  /** First letter for the placeholder shown when a camera is off. */
  initial(name: string | undefined): string {
    return (name?.trim()[0] ?? '?').toUpperCase();
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

  /**
   * Returns a stable MediaStream for the participant's microphone, or null.
   *
   * This has to exist separately from the video stream and feed its own <audio> element. The video
   * element's stream carries only the video track, so without this the remote audio track is
   * subscribed and decoded but never routed to an output device — the call looks connected and is
   * completely silent.
   *
   * Muted publications are deliberately still returned: LiveKit keeps the track through a mute, and
   * dropping the stream here would tear the element down and force a fresh one on every unmute.
   */
  getRemoteAudioStream(participant: {
    identity: string;
    getTrackPublications: () => any[];
  }): MediaStream | null {
    const audioPub = participant.getTrackPublications().find((pub) => pub.kind === 'audio');
    const track = audioPub?.track?.mediaStreamTrack as MediaStreamTrack | undefined;

    if (!track) {
      this.remoteAudioStreamCache.delete(participant.identity);
      return null;
    }

    const cached = this.remoteAudioStreamCache.get(participant.identity);
    if (cached && cached.trackId === track.id) {
      return cached.stream;
    }

    const stream = new MediaStream([track]);
    this.remoteAudioStreamCache.set(participant.identity, { stream, trackId: track.id });
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
    this.togglePanel('chat');
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

  private setInviteState(connectionId: string, state: InviteState | null): void {
    this.inviteStates.update((current) => {
      const next = new Map(current);
      if (state) {
        next.set(connectionId, state);
      } else {
        next.delete(connectionId);
      }
      return next;
    });
    if (state !== 'ringing') {
      this.clearInviteTimer(connectionId);
    }
  }

  private clearInviteTimer(connectionId: string): void {
    const timer = this.inviteTimers.get(connectionId);
    if (timer) {
      clearTimeout(timer);
      this.inviteTimers.delete(connectionId);
    }
  }

  private clearInviteStates(): void {
    this.inviteTimers.forEach((timer) => clearTimeout(timer));
    this.inviteTimers.clear();
    this.inviteStates.set(new Map());
    this.lastInviteTarget = null;
  }

  private isWideScreen(): boolean {
    return typeof window !== 'undefined' && window.innerWidth >= 1100;
  }

  private bindSignalrCallbacks(): void {
    this.signalRService.setCallbacks({
      onIncomingInvite: (payload) => {
        this.incomingInvite.set(payload);
      },
      onInviteRinging: () => {
        // The ringing state is set when the invite is sent; nothing more to show here.
      },
      onInviteDeclined: (payload) => {
        this.ringingMessage.set('');
        this.setInviteState(payload.declinedByUserId, 'declined');
        this.toast.info(`${payload.declinedByUsername} declined the call.`);

        // Only abandon a call this client started for that one person, and only if nobody is in
        // it and nobody else is still being rung. Leaving unconditionally (as this used to) meant
        // that one person declining an invite to an existing call dropped the inviter out of it.
        const stillRinging = [...this.inviteStates().values()].includes('ringing');
        if (
          payload.meetingId === this.startedMeetingId &&
          payload.meetingId === this.currentMeetingId() &&
          this.remoteParticipants().length === 0 &&
          !stillRinging
        ) {
          this.startedMeetingId = null;
          void this.livekit.leaveMeeting();
          void this.signalRService.setLeftCall();
        }
      },
      onInviteAccepted: (payload) => {
        this.ringingMessage.set('');
        // The hub tells both sides; only the inviter needs to react.
        if (payload.acceptedByUserId !== this.currentUserConnectionId()) {
          // They drop off the "add" list on their own once their presence shows them in this
          // meeting; clearing the state means a later re-invite starts from scratch.
          this.setInviteState(payload.acceptedByUserId, null);
          if (payload.meetingId === this.currentMeetingId()) {
            this.toast.success(`${payload.acceptedByUsername} is joining the call.`);
          }
        }
      },
      onCallFailed: (message) => {
        this.ringingMessage.set('');
        if (this.lastInviteTarget && this.inviteStates().get(this.lastInviteTarget) === 'ringing') {
          this.setInviteState(this.lastInviteTarget, 'failed');
        }
        this.toast.error(message);
      },
      onChatMessageReceived: (payload) => {
        if (payload.meetingId !== this.currentMeetingId()) {
          return;
        }
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
