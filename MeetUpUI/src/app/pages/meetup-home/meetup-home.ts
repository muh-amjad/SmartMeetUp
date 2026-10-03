import { CommonModule, Location } from '@angular/common';
import {
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
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { distinctUntilChanged, firstValueFrom, map } from 'rxjs';
import { ChatMessageDto } from '../../dtos/meetings/chat-message.dto';
import { readCallPageState } from '../../models/call-page-state';
import { User } from '../../models/user.model';
import { AuthService } from '../../services/auth.service';
import { LivekitMeetingService } from '../../services/livekit-meeting.service';
import { MeetingApiService } from '../../services/meeting-api.service';
import { SignalrService } from '../../services/signalr.service';
import { ToastService } from '../../services/toast.service';
import { UsersFacade } from '../../store/facades/users.facade';

/** Where an invite sent from inside a call stands, per invited user. */
type InviteState = 'ringing' | 'declined' | 'failed';

/** The single side panel open next to the video grid — one at a time keeps mobile usable. */
type CallPanel = 'chat' | 'people';

/** The person this meeting was started to call, while it is still just the two of you. */
type Callee = { connectionId: string; username: string };

/**
 * How long an invite shows as ringing if the server never reports back. The server's own
 * answer (accepted, declined or missed after 45s) normally arrives well before this.
 */
const InviteRingTimeoutMs = 60_000;

/**
 * The meeting page. Every call happens here, on its own page outside the app shell:
 *  - `/meet/:meetingId` joins that meeting (an accepted invite, a rejoin, a refresh);
 *  - `/meet` with router state starts a new meeting, and optionally rings one person.
 *
 * Inside a meeting the only way to bring someone in is "Add people", which invites them into this
 * same meeting. Calling someone separately means leaving the meeting first — there is no way to
 * start a second call from here. Incoming calls ring app-wide (IncomingCallDialog); accepting one
 * routes back here with the new meeting id, and this page switches over.
 */
@Component({
  selector: 'app-meetup-home',
  standalone: true,
  templateUrl: './meetup-home.html',
  styleUrl: './meetup-home.css',
  imports: [CommonModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetupHome implements OnInit, OnDestroy {
  private readonly usersFacade = inject(UsersFacade);
  private readonly signalRService = inject(SignalrService);
  private readonly authService = inject(AuthService);
  private readonly livekit = inject(LivekitMeetingService);
  private readonly meetingApi = inject(MeetingApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly location = inject(Location);
  private readonly destroyRef = inject(DestroyRef);

  readonly mediaError = signal('');

  /** Why the meeting could not be opened, or why the user is no longer in it. */
  readonly failure = signal('');

  /** True while connecting to a meeting (or creating one). */
  readonly joining = signal(false);

  readonly allUsers = this.usersFacade.users;
  readonly currentUsername = computed(() => this.authService.currentUser()?.username ?? '');

  readonly currentUserConnectionId = signal('');

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

  /** Invites sent from this call, keyed by the invitee's connection id. */
  readonly inviteStates = signal<ReadonlyMap<string, InviteState>>(new Map());
  readonly peopleFilter = signal('');

  /** Who this meeting was started to call; cleared once they answer or the call is given up. */
  readonly callee = signal<Callee | null>(null);

  /** Shown on the stage while the person being called is still ringing. */
  readonly ringingCallee = computed(() => {
    const callee = this.callee();
    return callee && this.inviteStates().get(callee.connectionId) === 'ringing' ? callee : null;
  });

  /**
   * Online users who could be added to the current call: not you (on any device) and not already
   * in this meeting. Anyone free comes first; people in another call can still be invited and
   * decide for themselves whether to switch.
   */
  readonly addableUsers = computed(() => {
    const meetingId = this.currentMeetingId()?.toLowerCase();
    const myIdentity = this.localParticipant()?.identity;
    const myConnectionId = this.currentUserConnectionId();
    const filter = this.peopleFilter().trim().toLowerCase();

    return this.allUsers()
      .filter((u) => u.id !== myConnectionId)
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
  private destroyed = false;

  /** The meeting this page created, so it can be abandoned if the only person called says no. */
  private startedMeetingId: string | null = null;

  /** The last meeting this page was in, for "Rejoin" after a dropped connection. */
  private lastMeetingId: string | null = null;

  /** Who the most recent in-call invite went to; CallFailed does not say. */
  private lastInviteTarget: string | null = null;

  private readonly inviteTimers = new Map<string, ReturnType<typeof setTimeout>>();

  private remoteStreamCache = new Map<string, { stream: MediaStream; trackId: string }>();

  // Audio is cached separately from video on purpose. A participant can turn their camera off while
  // still talking, and if both shared one stream the audio element would lose its source the moment
  // the video track went away.
  private remoteAudioStreamCache = new Map<string, { stream: MediaStream; trackId: string }>();

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

    // Dropped out of the room without hanging up (network loss, the meeting was ended): say so,
    // with a way back in, rather than leaving a blank page.
    let wasInCall = false;
    effect(() => {
      const inCall = this.inCall();
      untracked(() => {
        if (wasInCall && !inCall && !this.joining() && !this.isEndingCall && !this.destroyed) {
          this.failure.set('You were disconnected from the meeting.');
        }
        wasInCall = inCall;
      });
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
      this.destroyRef.onDestroy(() => narrowPortrait.removeEventListener('change', onChange));
    }
  }

  async ngOnInit(): Promise<void> {
    this.bindSignalrCallbacks();
    this.joining.set(true);

    try {
      // Usually already connected by the app; this waits for that rather than starting another.
      await this.signalRService.connectAndJoin();
    } catch (err) {
      console.error('Could not connect to the call service', err);
      this.fail('Could not connect to the call service. Check your connection and try again.');
      return;
    }
    if (this.destroyed) {
      return;
    }
    this.currentUserConnectionId.set(this.signalRService.connectionId);

    // Accepting an invite while on this page navigates to /meet/:otherId, which reuses this
    // component — so the meeting id is followed, not just read once.
    this.route.paramMap
      .pipe(
        map((params) => params.get('meetingId')),
        distinctUntilChanged(),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((meetingId) => {
        if (meetingId) {
          void this.enterMeeting(meetingId);
        } else {
          void this.startFromState();
        }
      });
  }

  ngOnDestroy(): void {
    this.destroyed = true;

    if (!this.isEndingCall) {
      // Also covers leaving while still connecting: the leave is queued behind the join.
      void this.livekit
        .leaveMeeting()
        .finally(() => this.signalRService.setLeftCall())
        .catch(() => undefined);
    }

    this.clearInviteStates();
    this.signalRService.setCallbacks({});
  }

  /** Joins an existing meeting, leaving any meeting this page was in. */
  private async enterMeeting(meetingId: string): Promise<void> {
    if (meetingId === this.currentMeetingId()) {
      this.joining.set(false);
      return;
    }

    this.resetForNewMeeting();
    this.joining.set(true);

    try {
      await this.livekit.joinMeeting(meetingId);
      if (this.destroyed) {
        return;
      }
      await this.signalRService.setInCall(meetingId);
      this.lastMeetingId = meetingId;
      await this.loadChatHistory(meetingId);
    } catch (err) {
      console.error('joinMeeting failed', err);
      if (!this.destroyed) {
        this.fail('Could not join this meeting. It may have ended.');
      }
    } finally {
      this.joining.set(false);
    }
  }

  /** `/meet` without an id: start the meeting the router state asks for, or go back. */
  private async startFromState(): Promise<void> {
    const state = readCallPageState(history.state);
    if (!state) {
      // Nothing to show: there is no lobby, and calls are started from the dashboard.
      this.joining.set(false);
      await this.router.navigate(['/dashboard'], { replaceUrl: true });
      return;
    }

    const callee = state.source === 'call' ? state.invitee : null;
    this.resetForNewMeeting();
    this.callee.set(callee);
    this.joining.set(true);

    try {
      const created = await firstValueFrom(this.meetingApi.create());
      if (this.destroyed) {
        return;
      }
      this.startedMeetingId = created.meetingId;
      // Give the page its real address without re-routing: a refresh rejoins this meeting rather
      // than creating another one (and ringing the same person again).
      this.location.replaceState(`/meet/${created.meetingId}`);

      await this.livekit.joinMeeting(created.meetingId);
      if (this.destroyed) {
        return;
      }
      await this.signalRService.setInCall(created.meetingId);
      this.lastMeetingId = created.meetingId;

      if (callee) {
        await this.ring(callee, created.meetingId);
      }
      await this.loadChatHistory(created.meetingId);
    } catch (err) {
      console.error('Starting the meeting failed', err);
      if (!this.destroyed) {
        this.callee.set(null);
        this.fail(callee ? `Could not call ${callee.username}.` : 'Could not start the meeting.');
      }
    } finally {
      this.joining.set(false);
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
    await this.ring({ connectionId: user.id, username: user.username }, meetingId);
  }

  private async ring(person: Callee, meetingId: string): Promise<void> {
    this.setInviteState(person.connectionId, 'ringing');
    this.lastInviteTarget = person.connectionId;

    // A fallback only: the server reports a missed call itself. This frees the button (and gives
    // up a call nobody answered) if that report never arrives.
    this.clearInviteTimer(person.connectionId);
    this.inviteTimers.set(
      person.connectionId,
      setTimeout(() => {
        if (this.inviteStates().get(person.connectionId) === 'ringing') {
          this.setInviteState(person.connectionId, null);
          this.giveUpIfNobodyCame(person.connectionId, `${person.username} didn't answer.`);
        }
      }, InviteRingTimeoutMs),
    );

    try {
      await this.signalRService.inviteToMeeting(person.connectionId, meetingId);
    } catch (err) {
      this.setInviteState(person.connectionId, 'failed');
      this.toast.error(`Could not call ${person.username}.`);
      console.error('Inviting failed', err);
      this.giveUpIfNobodyCame(person.connectionId, null);
    }
  }

  inviteStateFor(user: User): InviteState | undefined {
    return this.inviteStates().get(user.id);
  }

  async endCall(): Promise<void> {
    this.isEndingCall = true;

    await this.livekit.leaveMeeting();
    await this.signalRService.setLeftCall();
    this.chatMessages.set([]);
    this.chatDraft.set('');
    this.startedMeetingId = null;
    this.callee.set(null);

    // Replaced, so Back from the dashboard doesn't land in the call that was just left.
    await this.router.navigate(['/dashboard'], { replaceUrl: true });
  }

  /** Back out of a meeting that is still connecting, or could not be opened. */
  goToDashboard(): void {
    void this.router.navigate(['/dashboard'], { replaceUrl: true });
  }

  /** After a dropped connection: try the same meeting again. */
  rejoin(): void {
    const meetingId = this.lastMeetingId;
    if (!meetingId) {
      this.goToDashboard();
      return;
    }
    this.failure.set('');
    void this.enterMeeting(meetingId);
  }

  canRejoin(): boolean {
    return !!this.lastMeetingId;
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

  private fail(message: string): void {
    this.joining.set(false);
    this.failure.set(message);
  }

  private resetForNewMeeting(): void {
    this.failure.set('');
    this.mediaError.set('');
    this.chatMessages.set([]);
    this.chatDraft.set('');
    this.startedMeetingId = null;
    this.callee.set(null);
    this.clearInviteStates();
  }

  /**
   * Like hanging up a phone nobody picked up: a meeting this page started just to call one
   * person is closed when that person can't or won't come, and the user goes back to the
   * dashboard. Only when nobody else is in it and nobody else is still being rung — someone
   * declining an invite to a call that is already going must never drop the inviter out of it.
   */
  private giveUpIfNobodyCame(connectionId: string, message: string | null): boolean {
    const callee = this.callee();
    const stillRinging = [...this.inviteStates().values()].includes('ringing');
    if (
      !callee ||
      callee.connectionId !== connectionId ||
      !this.startedMeetingId ||
      this.startedMeetingId !== this.currentMeetingId() ||
      this.remoteParticipants().length > 0 ||
      stillRinging
    ) {
      return false;
    }

    if (message) {
      this.toast.info(message);
    }
    void this.endCall();
    return true;
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
      onInviteRinging: () => {
        // The ringing state is set when the invite is sent; nothing more to show here.
      },
      onInviteDeclined: (payload) => {
        if (payload.meetingId !== this.currentMeetingId()) {
          return;
        }
        this.setInviteState(payload.declinedByUserId, 'declined');
        const message = `${payload.declinedByUsername} declined the call.`;
        if (!this.giveUpIfNobodyCame(payload.declinedByUserId, message)) {
          this.toast.info(message);
        }
      },
      onInviteMissed: (payload) => {
        if (payload.meetingId !== this.currentMeetingId()) {
          return;
        }
        this.setInviteState(payload.missedByUserId, null);
        const message = `${payload.missedByUsername} didn't answer.`;
        if (!this.giveUpIfNobodyCame(payload.missedByUserId, message)) {
          this.toast.info(message);
        }
      },
      onInviteAccepted: (payload) => {
        // The hub tells both sides; only the inviter needs to react.
        if (payload.acceptedByUserId === this.currentUserConnectionId()) {
          return;
        }
        // They drop off the "add" list on their own once their presence shows them in this
        // meeting; clearing the state means a later re-invite starts from scratch.
        this.setInviteState(payload.acceptedByUserId, null);
        if (this.callee()?.connectionId === payload.acceptedByUserId) {
          this.callee.set(null);
        }
        if (payload.meetingId === this.currentMeetingId()) {
          this.toast.success(`${payload.acceptedByUsername} is joining the call.`);
        }
      },
      onCallFailed: (message) => {
        const target = this.lastInviteTarget;
        if (target && this.inviteStates().get(target) === 'ringing') {
          this.setInviteState(target, 'failed');
        }
        this.toast.error(message);
        if (target) {
          this.giveUpIfNobodyCame(target, null);
        }
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
