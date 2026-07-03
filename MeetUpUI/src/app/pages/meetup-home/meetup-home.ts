import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  effect,
  computed,
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
import { UsersFacade } from '../../store/facades/users.facade';
import { CallFacade } from '../../store/facades/call.facade';

type RemoteVideoItem = {
  userId: string;
  username: string;
  stream: MediaStream;
  isCameraOn: boolean;
  isMicOn: boolean;
};

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
  readonly remoteVideos = signal<RemoteVideoItem[]>([]);
  readonly searchResults = signal<UserSearchResultDto[]>([]);
  readonly searching = signal(false);
  readonly searchMessage = signal('Search by username or email to find someone and call if online.');

  readonly onlineUsers = computed(() => {
    const myConnectionId = this.currentUserConnectionId();
    return this.allUsers().filter((u) => u.id !== myConnectionId);
  });

  private peerConnections = new Map<string, RTCPeerConnection>();
  private readonly remoteStreams = new Map<string, MediaStream>();
  private readonly remoteMediaStates = new Map<string, { isCameraOn: boolean; isMicOn: boolean }>();
  private readonly pendingIceCandidates = new Map<string, RTCIceCandidateInit[]>();
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
        | {
            roomId: string;
            users: User[];
          }
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

    this.cleanupAllPeerConnections();
    this.signalRService.setCallbacks({});

    if (this.dashboardMode() === 'call') {
      this.meetingMediaService.stopStream();
    }
  }

  async searchUsers() {
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

  callSearchedUser(result: UserSearchResultDto) {
    if (!result.isOnline || !result.connectionId) {
      window.alert('This user is currently offline.');
      return;
    }

    this.callUser(new UserDto(result.username, result.connectionId, result.userId, result.email));
  }

  callUser(user: User) {
    this.ringingMessage.set(`Ringing ${user.username}...`);
    void this.signalRService.startCall(user.id);
  }

  async endCall(): Promise<void> {
    this.isEndingCall = true;
    await this.signalRService.leaveCall();
    this.cleanupAllPeerConnections();
    this.currentRoomId.set(null);
    this.remoteVideos.set([]);
    this.ringingMessage.set('');
    this.incomingCall.set(null);
    this.meetingMediaService.stopStream();
    await this.router.navigate(['/dashboard']);
  }

  async logout() {
    await this.endCall();
    await this.signalRService.disconnect();
    this.authService.logout();
  }

  goHome() {
    this.router.navigate(['/']);
  }

  openSupport() {
    window.alert('Support chat widget is ready. We can wire behavior in the next step.');
  }

  startInstantMeeting() {
    this.router.navigate(['/preview']);
  }

  toggleCamera() {
    this.meetingMediaService.toggleCamera();
    void this.publishLocalMediaState();
  }

  toggleMic() {
    this.meetingMediaService.toggleMic();
    void this.syncAudioSenderState();
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

  acceptIncomingCall() {
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

  rejectIncomingCall() {
    const incomingCall = this.incomingCall();
    if (!incomingCall) {
      return;
    }

    void this.signalRService.respondToCall(incomingCall.inviteId, false);
    this.incomingCall.set(null);
  }

  private bindSignalrCallbacks() {
    this.signalRService.setCallbacks({
      onIncomingCall: (payload) => {
        this.incomingCall.set(payload);
      },
      onCallDeclined: (payload) => {
        this.ringingMessage.set('');
        window.alert(`${payload.declinedByUsername} declined the call.`);
      },
      onCallAccepted: (payload) => {
        this.ringingMessage.set('');
        this.currentRoomId.set(payload.roomId);

        if (this.dashboardMode() === 'dashboard') {
          this.router.navigate(['/meet'], {
            state: {
              autoStartMedia: true,
              callAcceptedPayload: payload,
            },
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
            state: {
              autoStartMedia: true,
              callAcceptedPayload: payload,
            },
          });
          return;
        }

        void this.syncParticipants(payload.users);
      },
      onReceiveCallOffer: (offer) => {
        void this.handleOffer(offer);
      },
      onReceiveCallAnswer: (answer) => {
        void this.handleAnswer(answer);
      },
      onReceiveCandidate: (candidatePayload) => {
        void this.handleCandidate(candidatePayload);
      },
      onMediaStateUpdated: (payload) => {
        if (payload.userId === this.currentUserConnectionId()) {
          return;
        }

        this.remoteMediaStates.set(payload.userId, {
          isCameraOn: payload.isCameraOn,
          isMicOn: payload.isMicOn,
        });

        this.remoteVideos.update((videos) =>
          videos.map((video) => {
            if (video.userId !== payload.userId) {
              return video;
            }

            return {
              ...video,
              isCameraOn: payload.isCameraOn,
              isMicOn: payload.isMicOn,
            };
          }),
        );
      },
    });
  }

  private async syncParticipants(users: User[]) {
    await this.ensureLocalMedia();
    this.currentUserConnectionId.set(this.signalRService.connectionId);

    const remoteUsers = users.filter((u) => u.id !== this.currentUserConnectionId());
    const remoteIds = new Set(remoteUsers.map((u) => u.id));

    for (const user of remoteUsers) {
      const existing = this.peerConnections.get(user.id);
      let peer = existing;
      if (peer && peer.connectionState === 'closed') {
        this.removeRemotePeer(user.id);
        peer = undefined;
      }

      if (!peer) {
        peer = this.createPeerConnection(user.id, user.username);
      }

      const shouldInitiate = this.currentUserConnectionId().localeCompare(user.id) > 0;
      if (shouldInitiate && peer.signalingState === 'stable') {
        await this.createOfferFor(user.id);
      }
    }

    for (const [remoteId] of this.peerConnections) {
      if (!remoteIds.has(remoteId)) {
        this.removeRemotePeer(remoteId);
      }
    }

    await this.publishLocalMediaState();
  }

  private createPeerConnection(remoteUserId: string, remoteUsername: string): RTCPeerConnection {
    const localStream = this.meetingMediaService.localStream();
    if (!localStream) {
      throw new Error('Local media stream is not available.');
    }

    const connection = new RTCPeerConnection({
      iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
    });

    localStream.getTracks().forEach((track) => {
      connection.addTrack(track, localStream);
    });

    connection.ontrack = (event) => {
      const track = event.track;
      const stream = event.streams[0];
      this.remoteStreams.set(remoteUserId, stream);

      if (track.kind === 'video') {
        this.setRemoteTrackState(remoteUserId, 'video', !track.muted && track.readyState === 'live');
      }

      if (track.kind === 'audio') {
        this.setRemoteTrackState(remoteUserId, 'audio', !track.muted && track.readyState === 'live');
      }

      track.onmute = () => {
        this.setRemoteTrackState(remoteUserId, track.kind, false);
      };

      track.onunmute = () => {
        this.setRemoteTrackState(remoteUserId, track.kind, true);
      };

      track.onended = () => {
        this.setRemoteTrackState(remoteUserId, track.kind, false);
      };

      this.updateRemoteVideos(remoteUserId, remoteUsername, stream);
    };

    connection.onicecandidate = (event) => {
      if (event.candidate && this.currentRoomId()) {
        void this.signalRService.sendIceCandidate(this.currentRoomId()!, remoteUserId, event.candidate.toJSON());
      }
    };

    connection.onconnectionstatechange = () => {
      if (connection.connectionState === 'failed' || connection.connectionState === 'disconnected') {
        this.removeRemotePeer(remoteUserId);
      }
    };

    this.peerConnections.set(remoteUserId, connection);
    return connection;
  }

  private async createOfferFor(remoteUserId: string) {
    const peer = this.peerConnections.get(remoteUserId);
    if (!peer || !this.currentRoomId()) {
      return;
    }

    const offer = await peer.createOffer();
    await peer.setLocalDescription(offer);

    if (peer.localDescription) {
      await this.signalRService.sendCallOffer(this.currentRoomId()!, remoteUserId, peer.localDescription);
    }
  }

  private async handleOffer(offer: {
    from: string;
    to: string;
    roomId: string;
    offer: RTCSessionDescriptionInit;
  }) {
    if (this.currentRoomId() && this.currentRoomId() !== offer.roomId) {
      return;
    }

    await this.ensureLocalMedia();

    if (!this.currentRoomId()) {
      this.currentRoomId.set(offer.roomId);
    }

    let peer = this.peerConnections.get(offer.from);
    if (!peer) {
      const remoteUser = this.allUsers().find((u) => u.id === offer.from);
      peer = this.createPeerConnection(offer.from, remoteUser?.username ?? 'User');
    }

    if (peer.signalingState === 'have-local-offer') {
      await peer.setLocalDescription({ type: 'rollback' });
    }

    await peer.setRemoteDescription(new RTCSessionDescription(offer.offer));
    await this.flushQueuedCandidates(offer.from);

    const answer = await peer.createAnswer();
    await peer.setLocalDescription(answer);

    if (peer.localDescription) {
      await this.signalRService.sendCallAnswer(this.currentRoomId()!, offer.from, peer.localDescription);
    }
  }

  private async handleAnswer(answer: {
    from: string;
    to: string;
    roomId: string;
    offer: RTCSessionDescriptionInit;
  }) {
    if (this.currentRoomId() && this.currentRoomId() !== answer.roomId) {
      return;
    }

    const peer = this.peerConnections.get(answer.from);
    if (!peer) {
      return;
    }

    await peer.setRemoteDescription(new RTCSessionDescription(answer.offer));
    await this.flushQueuedCandidates(answer.from);
  }

  private async handleCandidate(payload: {
    roomId: string;
    from: string;
    to: string;
    candidate: RTCIceCandidateInit;
  }) {
    if (this.currentRoomId() && this.currentRoomId() !== payload.roomId) {
      return;
    }

    const peer = this.peerConnections.get(payload.from);
    if (!peer) {
      this.queueCandidate(payload.from, payload.candidate);
      return;
    }

    if (!peer.remoteDescription) {
      this.queueCandidate(payload.from, payload.candidate);
      return;
    }

    await peer.addIceCandidate(new RTCIceCandidate(payload.candidate));
  }

  private updateRemoteVideos(remoteUserId: string, remoteUsername: string, stream: MediaStream) {
    const videoTrack = stream.getVideoTracks()[0];
    const audioTrack = stream.getAudioTracks()[0];

    const trackedState = this.remoteMediaStates.get(remoteUserId);
    const isCameraOn =
      trackedState?.isCameraOn ?? (!!videoTrack && !videoTrack.muted && videoTrack.readyState === 'live');
    const isMicOn = trackedState?.isMicOn ?? (!!audioTrack && !audioTrack.muted && audioTrack.readyState === 'live');

    this.remoteVideos.update((videos) => {
      const current = videos.filter((video) => video.userId !== remoteUserId);
      return [...current, { userId: remoteUserId, username: remoteUsername, stream, isCameraOn, isMicOn }];
    });
  }

  private setRemoteTrackState(remoteUserId: string, kind: string, isOn: boolean) {
    const explicitState = this.remoteMediaStates.get(remoteUserId);
    if (explicitState) {
      return;
    }

    this.remoteVideos.update((videos) =>
      videos.map((video) => {
        if (video.userId !== remoteUserId) {
          return video;
        }

        if (kind === 'video') {
          return { ...video, isCameraOn: isOn };
        }

        if (kind === 'audio') {
          return { ...video, isMicOn: isOn };
        }

        return video;
      }),
    );
  }

  private removeRemotePeer(remoteUserId: string) {
    const connection = this.peerConnections.get(remoteUserId);
    if (connection) {
      connection.close();
    }

    this.peerConnections.delete(remoteUserId);
    this.remoteStreams.delete(remoteUserId);
    this.remoteMediaStates.delete(remoteUserId);
    this.pendingIceCandidates.delete(remoteUserId);
    this.remoteVideos.update((videos) => videos.filter((video) => video.userId !== remoteUserId));
  }

  private cleanupAllPeerConnections() {
    for (const [remoteUserId] of this.peerConnections) {
      this.removeRemotePeer(remoteUserId);
    }

    this.peerConnections.clear();
    this.remoteStreams.clear();
    this.remoteMediaStates.clear();
    this.pendingIceCandidates.clear();
    this.remoteVideos.set([]);
    this.callFacade.updateCallState(false);
  }

  private async publishLocalMediaState(): Promise<void> {
    const roomId = this.currentRoomId();
    if (!roomId || this.dashboardMode() !== 'call') {
      return;
    }

    await this.signalRService.sendMediaState(roomId, this.meetingMediaService.isCameraOn(), this.meetingMediaService.isMicOn());
  }

  private async syncAudioSenderState(): Promise<void> {
    const localStream = this.meetingMediaService.localStream();
    if (!localStream) {
      return;
    }

    const localAudioTrack = localStream.getAudioTracks()[0] ?? null;
    const shouldSendAudio = this.meetingMediaService.isMicOn();

    for (const peer of this.peerConnections.values()) {
      const audioTransceiver = peer
        .getTransceivers()
        .find((transceiver) => transceiver.receiver?.track?.kind === 'audio' || transceiver.sender?.track?.kind === 'audio');

      if (!audioTransceiver) {
        continue;
      }

      await audioTransceiver.sender.replaceTrack(shouldSendAudio ? localAudioTrack : null);
    }
  }

  private queueCandidate(remoteUserId: string, candidate: RTCIceCandidateInit) {
    const existing = this.pendingIceCandidates.get(remoteUserId) ?? [];
    existing.push(candidate);
    this.pendingIceCandidates.set(remoteUserId, existing);
  }

  private async flushQueuedCandidates(remoteUserId: string) {
    const peer = this.peerConnections.get(remoteUserId);
    const queued = this.pendingIceCandidates.get(remoteUserId);

    if (!peer || !peer.remoteDescription || !queued?.length) {
      return;
    }

    for (const candidate of queued) {
      await peer.addIceCandidate(new RTCIceCandidate(candidate));
    }

    this.pendingIceCandidates.delete(remoteUserId);
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

  get isCameraOn(): boolean {
    return this.meetingMediaService.isCameraOn();
  }

  get isMicOn(): boolean {
    return this.meetingMediaService.isMicOn();
  }

  trackByRemoteId(_: number, item: { userId: string }): string {
    return item.userId;
  }
}
