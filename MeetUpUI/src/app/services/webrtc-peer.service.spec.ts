import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { PeerServiceCallbacks, WebrtcPeerService } from './webrtc-peer.service';

function createRemoteStream(videoMuted: boolean, audioMuted: boolean): MediaStream {
  const videoTrack = { muted: videoMuted, readyState: 'live' };
  const audioTrack = { muted: audioMuted, readyState: 'live' };

  return {
    getVideoTracks: () => [videoTrack],
    getAudioTracks: () => [audioTrack],
  } as unknown as MediaStream;
}

function buildCallbacks(overrides: Partial<PeerServiceCallbacks> = {}): PeerServiceCallbacks {
  return {
    sendOffer: vi.fn().mockResolvedValue(undefined),
    sendAnswer: vi.fn().mockResolvedValue(undefined),
    sendIceCandidate: vi.fn().mockResolvedValue(undefined),
    resolveRemoteUsername: vi.fn().mockReturnValue('Remote User'),
    ...overrides,
  };
}

describe('WebrtcPeerService', () => {
  let service: WebrtcPeerService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(WebrtcPeerService);
    service.configure(buildCallbacks());
  });

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('overrides derived state when an explicit media-state update is received', () => {
    const streamA = createRemoteStream(false, false);
    const streamB = createRemoteStream(false, false);

    // seed two remote videos
    (service as unknown as { remoteVideosSignal: { set: (v: unknown[]) => void } }).remoteVideosSignal.set([
      { userId: 'u1', username: 'User 1', stream: streamA, isCameraOn: true, isMicOn: true },
      { userId: 'u2', username: 'User 2', stream: streamB, isCameraOn: true, isMicOn: true },
    ]);

    service.updateRemoteMediaState('u1', false, true);

    const [u1, u2] = service.remoteVideos();
    expect(u1.isCameraOn).toBe(false);
    expect(u1.isMicOn).toBe(true);
    expect(u2.isCameraOn).toBe(true);
    expect(u2.isMicOn).toBe(true);

    service.updateRemoteMediaState('u1', false, false);
    expect(service.remoteVideos()[0].isMicOn).toBe(false);
  });

  it('closes peer connections and clears all tracked state on cleanup', () => {
    const closeSpy = vi.fn();
    const peerConnection = { close: closeSpy } as unknown as RTCPeerConnection;

    const internals = service as unknown as {
      peerConnections: Map<string, RTCPeerConnection>;
      remoteStreams: Map<string, MediaStream>;
      remoteMediaStates: Map<string, { isCameraOn: boolean; isMicOn: boolean }>;
      pendingIceCandidates: Map<string, RTCIceCandidateInit[]>;
      remoteVideosSignal: { set: (v: unknown[]) => void };
    };

    internals.peerConnections.set('u4', peerConnection);
    internals.remoteStreams.set('u4', createRemoteStream(false, false));
    internals.remoteMediaStates.set('u4', { isCameraOn: false, isMicOn: false });
    internals.pendingIceCandidates.set('u4', [{ candidate: 'x', sdpMid: '0', sdpMLineIndex: 0 }]);
    internals.remoteVideosSignal.set([
      { userId: 'u4', username: 'User 4', stream: createRemoteStream(false, false), isCameraOn: true, isMicOn: true },
    ]);

    service.cleanupAll();

    expect(closeSpy).toHaveBeenCalled();
    expect(internals.peerConnections.size).toBe(0);
    expect(internals.remoteStreams.size).toBe(0);
    expect(internals.remoteMediaStates.size).toBe(0);
    expect(internals.pendingIceCandidates.size).toBe(0);
    expect(service.remoteVideos().length).toBe(0);
  });
});
