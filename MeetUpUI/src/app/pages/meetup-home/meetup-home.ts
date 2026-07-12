import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { gsap } from 'gsap';
import { UserSearchResultDto } from '../../dtos/user-search-result.dto';
import { UserDto } from '../../dtos/user.dto';
import { User } from '../../models/user.model';
import { AuthService } from '../../services/auth.service';
import { MeetingMediaService } from '../../services/meeting-media.service';
import { SignalrService } from '../../services/signalr.service';
import { UserDirectoryService } from '../../services/user-directory.service';
import { WebrtcPeerService } from '../../services/webrtc-peer.service';
import { CallFacade } from '../../store/facades/call.facade';
import { UsersFacade } from '../../store/facades/users.facade';

@Component({
  selector: 'app-meetup-home',
  standalone: true,
  templateUrl: './meetup-home.html',
  styleUrl: './meetup-home.css',
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MeetupHome implements OnInit, AfterViewInit, OnDestroy {
  private readonly usersFacade = inject(UsersFacade);
  private readonly callFacade = inject(CallFacade);
  private readonly signalRService = inject(SignalrService);
  private readonly authService = inject(AuthService);
  private readonly meetingMediaService = inject(MeetingMediaService);
  private readonly userDirectoryService = inject(UserDirectoryService);
  private readonly peerService = inject(WebrtcPeerService);
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

  readonly currentRoomId = signal<string | null>(null);
  readonly currentUserConnectionId = signal('');

  readonly incomingCall = signal<{ inviteId: string; roomId: string; fromUserId: string; fromUsername: string } | null>(
    null,
  );
  readonly ringingMessage = signal('');
  readonly searchResults = signal<UserSearchResultDto[]>([]);
  readonly searching = signal(false);
  readonly searchMessage = signal('Search by username or email to find someone and call if online.');

  /** Remote videos are owned by the WebRTC peer service. */
  readonly remoteVideos = this.peerService.remoteVideos;

  readonly onlineUsers = computed(() => {
    const myConnectionId = this.currentUserConnectionId();
    return this.allUsers().filter((u) => u.id !== myConnectionId);
  });

  private isEndingCall = false;

  @ViewChild('pageShell', { static: true })
  pageShellRef!: ElementRef<HTMLElement>;

  @ViewChild('localVideo')
  localVideoRef?: ElementRef<HTMLVideoElement>;

  constructor() {
    effect(() => {
      const stream = this.meetingMediaService.localStream();
      if (stream) {
        void this.attachLocalPreview();
      }
    });

    this.peerService.configure({
      sendOffer: (roomId, targetId, offer) => this.signalRService.sendCallOffer(roomId, targetId, offer),
      sendAnswer: (roomId, targetId, answer) => this.signalRService.sendCallAnswer(roomId, targetId, answer),
      sendIceCandidate: (roomId, targetId, candidate) =>
        this.signalRService.sendIceCandidate(roomId, targetId, candidate),
      resolveRemoteUsername: (remoteUserId) =>
        this.allUsers().find((u) => u.id === remoteUserId)?.username ?? 'User',
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
      await this.ensureLocalMedia();

      const isPreviewJoin = history.state?.source === 'join-now';
      const shouldStartInstantMeeting = isPreviewJoin && !history.state?.callAcceptedPayload;
      if (shouldStartInstantMeeting) {
        await this.signalRService.startInstantMeeting();
      }

      const acceptedPayload = (history.state?.callAcceptedPayload ?? null) as
        | { roomId: string; users: User[] }
        | null;

      if (acceptedPayload?.roomId) {
        this.currentRoomId.set(acceptedPayload.roomId);
        await this.syncParticipants(acceptedPayload.users);
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

    if (this.dashboardMode() === 'call') {
      void this.attachLocalPreview();
    }
  }

  ngOnDestroy(): void {
    if (this.dashboardMode() === 'call' && this.currentRoomId() && !this.isEndingCall) {
      void this.signalRService.leaveCall();
    }

    this.peerService.cleanupAll();
    this.callFacade.updateCallState(false);
    this.signalRService.setCallbacks({});

    if (this.dashboardMode() === 'call') {
      this.meetingMediaService.stopStream();
    }
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
        this.searchMessage.set(results.length ? `Found ${results.length} user(s).` : 'No users matched your search.');
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

    this.callUser(new UserDto(result.username, result.connectionId, result.userId, result.email));
  }

  callUser(user: User): void {
    this.ringingMessage.set(`Ringing ${user.username}...`);
    void this.signalRService.startCall(user.id);
  }

  async endCall(): Promise<void> {
    this.isEndingCall = true;
    await this.signalRService.leaveCall();
    this.peerService.cleanupAll();
    this.callFacade.updateCallState(false);
    this.currentRoomId.set(null);
    this.ringingMessage.set('');
    this.incomingCall.set(null);
    this.meetingMediaService.stopStream();
    await this.router.navigate(['/dashboard']);
  }

  async logout(): Promise<void> {
    await this.endCall();
    await this.signalRService.disconnect();
    this.authService.logout();
  }

  goHome(): void {
    this.router.navigate(['/']);
  }

  openSupport(): void {
    window.alert('Support chat widget is ready. We can wire behavior in the next step.');
  }

  startInstantMeeting(): void {
    this.router.navigate(['/preview']);
  }

  toggleCamera(): void {
    this.meetingMediaService.toggleCamera();
    void this.publishLocalMediaState();
  }

  toggleMic(): void {
    this.meetingMediaService.toggleMic();
    void this.syncLocalAudioSender();
    void this.publishLocalMediaState();
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
    const incomingCall = this.incomingCall();
    if (!incomingCall) {
      return;
    }

    if (this.dashboardMode() === 'dashboard') {
      this.router.navigate(['/meet'], { state: { autoStartMedia: true, source: 'incoming-call' } });
    }

    void this.signalRService.respondToCall(incomingCall.inviteId, true);
    this.incomingCall.set(null);
  }

  rejectIncomingCall(): void {
    const incomingCall = this.incomingCall();
    if (!incomingCall) {
      return;
    }

    void this.signalRService.respondToCall(incomingCall.inviteId, false);
    this.incomingCall.set(null);
  }

  trackByRemoteId(_: number, item: { userId: string }): string {
    return item.userId;
  }

  get isCameraOn(): boolean {
    return this.meetingMediaService.isCameraOn();
  }

  get isMicOn(): boolean {
    return this.meetingMediaService.isMicOn();
  }

  private bindSignalrCallbacks(): void {
    this.signalRService.setCallbacks({
      onIncomingCall: (payload) => this.incomingCall.set(payload),
      onCallDeclined: (payload) => {
        this.ringingMessage.set('');
        window.alert(`${payload.declinedByUsername} declined the call.`);
      },
      onCallAccepted: (payload) => {
        this.ringingMessage.set('');
        this.currentRoomId.set(payload.roomId);

        if (this.dashboardMode() === 'dashboard') {
          this.router.navigate(['/meet'], {
            state: { autoStartMedia: true, callAcceptedPayload: payload },
          });
          return;
        }

        void this.ensureLocalMedia().then(() => {
          this.currentUserConnectionId.set(this.signalRService.connectionId);
          return this.syncParticipants(payload.users);
        });
      },
      onInstantMeetingStarted: (payload) => {
        this.currentRoomId.set(payload.roomId);
        this.currentUserConnectionId.set(this.signalRService.connectionId);
        void this.syncParticipants(payload.users);
      },
      onCallFailed: (message) => {
        this.ringingMessage.set('');
        window.alert(message);
      },
      onRoomParticipantsUpdated: (payload) => {
        if (this.currentRoomId() && this.currentRoomId() !== payload.roomId) {
          return;
        }

        this.currentRoomId.set(payload.roomId);
        if (this.dashboardMode() === 'dashboard' && this.isCurrentUserInRoom(payload.users)) {
          this.router.navigate(['/meet'], {
            state: { autoStartMedia: true, callAcceptedPayload: payload },
          });
          return;
        }

        void this.syncParticipants(payload.users);
      },
      onReceiveCallOffer: (offer) => {
        void this.handleIncomingOffer(offer);
      },
      onReceiveCallAnswer: (answer) => {
        void this.peerService.handleAnswer(answer);
      },
      onReceiveCandidate: (candidatePayload) => {
        void this.peerService.handleCandidate(candidatePayload);
      },
      onMediaStateUpdated: (payload) => {
        if (payload.userId === this.currentUserConnectionId()) {
          return;
        }

        this.peerService.updateRemoteMediaState(payload.userId, payload.isCameraOn, payload.isMicOn);
      },
    });
  }

  private async syncParticipants(users: User[]): Promise<void> {
    await this.ensureLocalMedia();
    this.currentUserConnectionId.set(this.signalRService.connectionId);

    const localStream = this.meetingMediaService.localStream();
    const roomId = this.currentRoomId();
    if (!localStream || !roomId) {
      return;
    }

    const remoteUsers = users.filter((u) => u.id !== this.currentUserConnectionId());
    await this.peerService.syncParticipants(remoteUsers, this.currentUserConnectionId(), roomId, localStream);
    await this.publishLocalMediaState();
  }

  private async handleIncomingOffer(offer: {
    from: string;
    to: string;
    roomId: string;
    offer: RTCSessionDescriptionInit;
  }): Promise<void> {
    if (this.currentRoomId() && this.currentRoomId() !== offer.roomId) {
      return;
    }

    await this.ensureLocalMedia();
    if (!this.currentRoomId()) {
      this.currentRoomId.set(offer.roomId);
    }

    const localStream = this.meetingMediaService.localStream();
    if (!localStream) {
      return;
    }

    await this.peerService.handleOffer(offer, this.currentRoomId()!, localStream);
  }

  private async publishLocalMediaState(): Promise<void> {
    const roomId = this.currentRoomId();
    if (!roomId || this.dashboardMode() !== 'call') {
      return;
    }

    await this.signalRService.sendMediaState(
      roomId,
      this.meetingMediaService.isCameraOn(),
      this.meetingMediaService.isMicOn(),
    );
  }

  private async syncLocalAudioSender(): Promise<void> {
    const localStream = this.meetingMediaService.localStream();
    if (!localStream) {
      return;
    }

    const localAudioTrack = localStream.getAudioTracks()[0] ?? null;
    await this.peerService.syncAudioSenderState(this.meetingMediaService.isMicOn(), localAudioTrack);
  }

  private isCurrentUserInRoom(users: Array<{ id: string }>): boolean {
    const connectionId = this.signalRService.connectionId;
    if (!connectionId) {
      return false;
    }

    return users.some((user) => user.id === connectionId);
  }

  private async ensureLocalMedia(): Promise<void> {
    if (this.dashboardMode() !== 'call') {
      return;
    }

    try {
      await this.meetingMediaService.ensureLocalStream();
      this.mediaError.set('');
      await this.attachLocalPreview();
    } catch {
      this.mediaError.set('Camera and microphone access is required for meetings.');
    }
  }

  private async attachLocalPreview(): Promise<void> {
    const localVideo = this.localVideoRef?.nativeElement;
    if (!localVideo || this.dashboardMode() !== 'call') {
      return;
    }

    await this.meetingMediaService.attachStream(localVideo);
  }
}
