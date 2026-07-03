import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { vi } from 'vitest';
import { AuthService } from '../../services/auth.service';
import { MeetingMediaService } from '../../services/meeting-media.service';
import { SignalrService } from '../../services/signalr.service';
import { UserDirectoryService } from '../../services/user-directory.service';
import { CallFacade } from '../../store/facades/call.facade';
import { UsersFacade } from '../../store/facades/users.facade';
import { MeetupHome } from './meetup-home';

describe('MeetupHome', () => {
  let component: MeetupHome;
  let fixture: ComponentFixture<MeetupHome>;

  function createRemoteStream(videoMuted: boolean, audioMuted: boolean): MediaStream {
    const videoTrack = { muted: videoMuted, readyState: 'live' };
    const audioTrack = { muted: audioMuted, readyState: 'live' };

    return {
      getVideoTracks: () => [videoTrack],
      getAudioTracks: () => [audioTrack],
    } as unknown as MediaStream;
  }

  beforeEach(async () => {
    const usersFacadeStub = {
      users: signal([] as Array<{ id: string; username: string; isInCall: boolean; email?: string }>),
      updateUserList: vi.fn(),
    };

    const callFacadeStub = {
      updateCallState: vi.fn(),
    };

    const signalrStub = {
      connectionId: 'self-connection',
      setCallbacks: vi.fn(),
      attachSignalRHandlers: vi.fn(),
      connectAndJoin: vi.fn().mockResolvedValue(undefined),
      startInstantMeeting: vi.fn().mockResolvedValue(undefined),
      startCall: vi.fn().mockResolvedValue(undefined),
      respondToCall: vi.fn().mockResolvedValue(undefined),
      leaveCall: vi.fn().mockResolvedValue(undefined),
      sendIceCandidate: vi.fn().mockResolvedValue(undefined),
      sendMediaState: vi.fn().mockResolvedValue(undefined),
      sendCallOffer: vi.fn().mockResolvedValue(undefined),
      sendCallAnswer: vi.fn().mockResolvedValue(undefined),
      disconnect: vi.fn().mockResolvedValue(undefined),
    };

    const meetingMediaStub = {
      localStream: signal<MediaStream | null>(null),
      ensureLocalStream: vi.fn().mockResolvedValue(createRemoteStream(false, false)),
      attachStream: vi.fn().mockResolvedValue(undefined),
      stopStream: vi.fn(),
      toggleCamera: vi.fn(),
      toggleMic: vi.fn(),
      isCameraOn: signal(true),
      isMicOn: signal(true),
    };

    const authServiceStub = {
      currentUser: vi.fn().mockReturnValue({ username: 'tester', email: 'tester@meetup.test' }),
      logout: vi.fn(),
    };

    const userDirectoryStub = {
      searchUsers: vi.fn().mockReturnValue(of([])),
    };

    const routerStub = {
      navigate: vi.fn().mockResolvedValue(true),
    };

    await TestBed.configureTestingModule({
      imports: [MeetupHome],
      providers: [
        { provide: UsersFacade, useValue: usersFacadeStub },
        { provide: CallFacade, useValue: callFacadeStub },
        { provide: SignalrService, useValue: signalrStub },
        { provide: MeetingMediaService, useValue: meetingMediaStub },
        { provide: AuthService, useValue: authServiceStub },
        { provide: UserDirectoryService, useValue: userDirectoryStub },
        { provide: Router, useValue: routerStub },
        { provide: ActivatedRoute, useValue: { snapshot: { data: { mode: 'call' } } } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MeetupHome);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('should update only targeted user track state', () => {
    const streamA = createRemoteStream(false, false);
    const streamB = createRemoteStream(false, false);

    component.remoteVideos.set([
      { userId: 'u1', username: 'User 1', stream: streamA, isCameraOn: true, isMicOn: true },
      { userId: 'u2', username: 'User 2', stream: streamB, isCameraOn: true, isMicOn: true },
    ]);

    (component as any).setRemoteTrackState('u1', 'video', false);

    const [u1, u2] = component.remoteVideos();
    expect(u1.isCameraOn).toBe(false);
    expect(u1.isMicOn).toBe(true);
    expect(u2.isCameraOn).toBe(true);
    expect(u2.isMicOn).toBe(true);

    (component as any).setRemoteTrackState('u1', 'audio', false);

    const [u1AfterAudio] = component.remoteVideos();
    expect(u1AfterAudio.isCameraOn).toBe(false);
    expect(u1AfterAudio.isMicOn).toBe(false);
  });

  it('should derive camera and mic status from incoming stream tracks', () => {
    const stream = createRemoteStream(true, false);

    (component as any).updateRemoteVideos('u3', 'User 3', stream);

    const [remote] = component.remoteVideos();
    expect(remote.userId).toBe('u3');
    expect(remote.username).toBe('User 3');
    expect(remote.isCameraOn).toBe(false);
    expect(remote.isMicOn).toBe(true);
  });

  it('should cleanup peer and remote state when participant leaves', () => {
    const closeSpy = vi.fn();
    const peerConnection = { close: closeSpy } as unknown as RTCPeerConnection;
    const stream = createRemoteStream(false, false);

    (component as any).peerConnections.set('u4', peerConnection);
    (component as any).remoteStreams.set('u4', stream);
    (component as any).remoteMediaStates.set('u4', { isCameraOn: false, isMicOn: false });
    (component as any).pendingIceCandidates.set('u4', [{ candidate: 'x', sdpMid: '0', sdpMLineIndex: 0 }]);
    component.remoteVideos.set([{ userId: 'u4', username: 'User 4', stream, isCameraOn: true, isMicOn: true }]);

    (component as any).removeRemotePeer('u4');

    expect(closeSpy).toHaveBeenCalled();
    expect((component as any).peerConnections.has('u4')).toBeFalsy();
    expect((component as any).remoteStreams.has('u4')).toBeFalsy();
    expect((component as any).remoteMediaStates.has('u4')).toBeFalsy();
    expect((component as any).pendingIceCandidates.has('u4')).toBeFalsy();
    expect(component.remoteVideos().length).toBe(0);
  });
});
