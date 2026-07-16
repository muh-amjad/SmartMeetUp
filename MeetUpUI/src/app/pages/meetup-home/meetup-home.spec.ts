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
  let peerServiceStub: {
    remoteVideos: ReturnType<typeof signal>;
    configure: ReturnType<typeof vi.fn>;
    cleanupAll: ReturnType<typeof vi.fn>;
    updateRemoteMediaState: ReturnType<typeof vi.fn>;
    syncParticipants: ReturnType<typeof vi.fn>;
    syncAudioSenderState: ReturnType<typeof vi.fn>;
    handleAnswer: ReturnType<typeof vi.fn>;
    handleOffer: ReturnType<typeof vi.fn>;
    handleCandidate: ReturnType<typeof vi.fn>;
  };

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
      ensureLocalStream: vi.fn().mockResolvedValue(undefined),
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

    peerServiceStub = {
      remoteVideos: signal([]),
      configure: vi.fn(),
      cleanupAll: vi.fn(),
      updateRemoteMediaState: vi.fn(),
      syncParticipants: vi.fn().mockResolvedValue(undefined),
      syncAudioSenderState: vi.fn().mockResolvedValue(undefined),
      handleAnswer: vi.fn().mockResolvedValue(undefined),
      handleOffer: vi.fn().mockResolvedValue(undefined),
      handleCandidate: vi.fn().mockResolvedValue(undefined),
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

  
  it('configures the peer service with signalling callbacks', () => {
    expect(peerServiceStub.configure).toHaveBeenCalledTimes(1);
    const callbacks = peerServiceStub.configure.mock.calls[0][0];
    expect(typeof callbacks.sendOffer).toBe('function');
    expect(typeof callbacks.sendAnswer).toBe('function');
    expect(typeof callbacks.sendIceCandidate).toBe('function');
    expect(typeof callbacks.resolveRemoteUsername).toBe('function');
  });
});
