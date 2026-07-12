import { Injectable, Signal, signal } from '@angular/core';

export interface RemoteVideoItem {
  userId: string;
  username: string;
  stream: MediaStream;
  isCameraOn: boolean;
  isMicOn: boolean;
}

export interface RemoteMediaState {
  isCameraOn: boolean;
  isMicOn: boolean;
}

export interface OfferPayload {
  from: string;
  to: string;
  roomId: string;
  offer: RTCSessionDescriptionInit;
}

export interface AnswerPayload {
  from: string;
  to: string;
  roomId: string;
  offer: RTCSessionDescriptionInit;
}

export interface CandidatePayload {
  roomId: string;
  from: string;
  to: string;
  candidate: RTCIceCandidateInit;
}

/**
 * Callbacks the component must wire to its signalling transport
 * (currently SignalR; will be replaced by LiveKit in phase 1).
 */
export interface PeerServiceCallbacks {
  sendOffer: (roomId: string, targetUserId: string, offer: RTCSessionDescriptionInit) => Promise<void> | void;
  sendAnswer: (roomId: string, targetUserId: string, answer: RTCSessionDescriptionInit) => Promise<void> | void;
  sendIceCandidate: (roomId: string, targetUserId: string, candidate: RTCIceCandidateInit) => Promise<void> | void;
  resolveRemoteUsername: (remoteUserId: string) => string;
}

const NOOP_CALLBACKS: PeerServiceCallbacks = {
  sendOffer: () => undefined,
  sendAnswer: () => undefined,
  sendIceCandidate: () => undefined,
  resolveRemoteUsername: () => 'User',
};

const DEFAULT_RTC_CONFIG: RTCConfiguration = {
  iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
};

/**
 * Owns every RTCPeerConnection for the current call and exposes the
 * derived `remoteVideos` signal that the meeting UI renders from.
 *
 * The component is a thin orchestrator: it forwards SignalR events into
 * this service and consumes {@link remoteVideos} for rendering.
 */
@Injectable({ providedIn: 'root' })
export class WebrtcPeerService {
  private readonly peerConnections = new Map<string, RTCPeerConnection>();
  private readonly remoteStreams = new Map<string, MediaStream>();
  private readonly remoteMediaStates = new Map<string, RemoteMediaState>();
  private readonly pendingIceCandidates = new Map<string, RTCIceCandidateInit[]>();

  private readonly remoteVideosSignal = signal<RemoteVideoItem[]>([]);
  readonly remoteVideos: Signal<RemoteVideoItem[]> = this.remoteVideosSignal.asReadonly();

  private callbacks: PeerServiceCallbacks = NOOP_CALLBACKS;

  configure(callbacks: PeerServiceCallbacks): void {
    this.callbacks = callbacks;
  }

  /**
   * Reconciles the set of open peer connections against the list of remote users
   * that should currently be in the room. Missing peers are created, and peers
   * whose remote user has left are torn down.
   */
  async syncParticipants(
    remoteUsers: Array<{ id: string; username: string }>,
    currentConnectionId: string,
    roomId: string,
    localStream: MediaStream,
  ): Promise<void> {
    const remoteIds = new Set(remoteUsers.map((u) => u.id));

    for (const user of remoteUsers) {
      let peer = this.peerConnections.get(user.id);
      if (peer && peer.connectionState === 'closed') {
        this.removeRemotePeer(user.id);
        peer = undefined;
      }

      if (!peer) {
        peer = this.createPeerConnection(user.id, user.username, roomId, localStream);
      }

      const shouldInitiate = currentConnectionId.localeCompare(user.id) > 0;
      if (shouldInitiate && peer.signalingState === 'stable') {
        await this.createOfferFor(user.id, roomId);
      }
    }

    for (const [remoteId] of this.peerConnections) {
      if (!remoteIds.has(remoteId)) {
        this.removeRemotePeer(remoteId);
      }
    }
  }

  async handleOffer(payload: OfferPayload, roomId: string, localStream: MediaStream): Promise<void> {
    let peer = this.peerConnections.get(payload.from);
    if (!peer) {
      const username = this.callbacks.resolveRemoteUsername(payload.from);
      peer = this.createPeerConnection(payload.from, username, roomId, localStream);
    }

    if (peer.signalingState === 'have-local-offer') {
      await peer.setLocalDescription({ type: 'rollback' });
    }

    await peer.setRemoteDescription(new RTCSessionDescription(payload.offer));
    await this.flushQueuedCandidates(payload.from);

    const answer = await peer.createAnswer();
    await peer.setLocalDescription(answer);

    if (peer.localDescription) {
      await this.callbacks.sendAnswer(roomId, payload.from, peer.localDescription);
    }
  }

  async handleAnswer(payload: AnswerPayload): Promise<void> {
    const peer = this.peerConnections.get(payload.from);
    if (!peer) {
      return;
    }

    await peer.setRemoteDescription(new RTCSessionDescription(payload.offer));
    await this.flushQueuedCandidates(payload.from);
  }

  async handleCandidate(payload: CandidatePayload): Promise<void> {
    const peer = this.peerConnections.get(payload.from);
    if (!peer || !peer.remoteDescription) {
      this.queueCandidate(payload.from, payload.candidate);
      return;
    }

    await peer.addIceCandidate(new RTCIceCandidate(payload.candidate));
  }

  /**
   * Applies an explicit media-state update received from a remote peer
   * (via the signalling channel). Takes precedence over track-derived state.
   */
  updateRemoteMediaState(userId: string, isCameraOn: boolean, isMicOn: boolean): void {
    this.remoteMediaStates.set(userId, { isCameraOn, isMicOn });

    this.remoteVideosSignal.update((videos) =>
      videos.map((video) => (video.userId === userId ? { ...video, isCameraOn, isMicOn } : video)),
    );
  }

  /**
   * Replaces the outbound audio track on every peer connection based on the
   * local mic-enabled state. Called when the local user toggles their mic.
   */
  async syncAudioSenderState(shouldSendAudio: boolean, localAudioTrack: MediaStreamTrack | null): Promise<void> {
    for (const peer of this.peerConnections.values()) {
      const audioTransceiver = peer
        .getTransceivers()
        .find(
          (transceiver) =>
            transceiver.receiver?.track?.kind === 'audio' || transceiver.sender?.track?.kind === 'audio',
        );

      if (!audioTransceiver) {
        continue;
      }

      await audioTransceiver.sender.replaceTrack(shouldSendAudio ? localAudioTrack : null);
    }
  }

  cleanupAll(): void {
    for (const [remoteUserId] of this.peerConnections) {
      this.removeRemotePeer(remoteUserId);
    }

    this.peerConnections.clear();
    this.remoteStreams.clear();
    this.remoteMediaStates.clear();
    this.pendingIceCandidates.clear();
    this.remoteVideosSignal.set([]);
  }

  private createPeerConnection(
    remoteUserId: string,
    remoteUsername: string,
    roomId: string,
    localStream: MediaStream,
  ): RTCPeerConnection {
    const connection = new RTCPeerConnection(DEFAULT_RTC_CONFIG);

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

      track.onmute = () => this.setRemoteTrackState(remoteUserId, track.kind, false);
      track.onunmute = () => this.setRemoteTrackState(remoteUserId, track.kind, true);
      track.onended = () => this.setRemoteTrackState(remoteUserId, track.kind, false);

      this.updateRemoteVideos(remoteUserId, remoteUsername, stream);
    };

    connection.onicecandidate = (event) => {
      if (event.candidate) {
        void this.callbacks.sendIceCandidate(roomId, remoteUserId, event.candidate.toJSON());
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

  private async createOfferFor(remoteUserId: string, roomId: string): Promise<void> {
    const peer = this.peerConnections.get(remoteUserId);
    if (!peer) {
      return;
    }

    const offer = await peer.createOffer();
    await peer.setLocalDescription(offer);

    if (peer.localDescription) {
      await this.callbacks.sendOffer(roomId, remoteUserId, peer.localDescription);
    }
  }

  private updateRemoteVideos(remoteUserId: string, remoteUsername: string, stream: MediaStream): void {
    const videoTrack = stream.getVideoTracks()[0];
    const audioTrack = stream.getAudioTracks()[0];

    const trackedState = this.remoteMediaStates.get(remoteUserId);
    const isCameraOn =
      trackedState?.isCameraOn ?? (!!videoTrack && !videoTrack.muted && videoTrack.readyState === 'live');
    const isMicOn = trackedState?.isMicOn ?? (!!audioTrack && !audioTrack.muted && audioTrack.readyState === 'live');

    this.remoteVideosSignal.update((videos) => {
      const filtered = videos.filter((video) => video.userId !== remoteUserId);
      return [...filtered, { userId: remoteUserId, username: remoteUsername, stream, isCameraOn, isMicOn }];
    });
  }

  private setRemoteTrackState(remoteUserId: string, kind: string, isOn: boolean): void {
    // If the peer has already sent an explicit media-state update, that wins.
    if (this.remoteMediaStates.has(remoteUserId)) {
      return;
    }

    this.remoteVideosSignal.update((videos) =>
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

  private removeRemotePeer(remoteUserId: string): void {
    const connection = this.peerConnections.get(remoteUserId);
    if (connection) {
      connection.close();
    }

    this.peerConnections.delete(remoteUserId);
    this.remoteStreams.delete(remoteUserId);
    this.remoteMediaStates.delete(remoteUserId);
    this.pendingIceCandidates.delete(remoteUserId);
    this.remoteVideosSignal.update((videos) => videos.filter((video) => video.userId !== remoteUserId));
  }

  private queueCandidate(remoteUserId: string, candidate: RTCIceCandidateInit): void {
    const existing = this.pendingIceCandidates.get(remoteUserId) ?? [];
    existing.push(candidate);
    this.pendingIceCandidates.set(remoteUserId, existing);
  }

  private async flushQueuedCandidates(remoteUserId: string): Promise<void> {
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
}
