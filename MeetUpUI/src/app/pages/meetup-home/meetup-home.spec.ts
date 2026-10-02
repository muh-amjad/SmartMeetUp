import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';
import { vi } from 'vitest';
import { AuthService } from '../../services/auth.service';
import { LivekitMeetingService } from '../../services/livekit-meeting.service';
import { MeetingApiService } from '../../services/meeting-api.service';
import { SignalrService } from '../../services/signalr.service';
import { UserDirectoryService } from '../../services/user-directory.service';
import { UsersFacade } from '../../store/facades/users.facade';
import { MeetupHome } from './meetup-home';

describe('MeetupHome', () => {
  let component: MeetupHome;
  let fixture: ComponentFixture<MeetupHome>;
  let signalrStub: {
    connectionId: string;
    setCallbacks: ReturnType<typeof vi.fn>;
    attachSignalRHandlers: ReturnType<typeof vi.fn>;
    connectAndJoin: ReturnType<typeof vi.fn>;
    inviteToMeeting: ReturnType<typeof vi.fn>;
    setInCall: ReturnType<typeof vi.fn>;
    setLeftCall: ReturnType<typeof vi.fn>;
    sendChatMessage: ReturnType<typeof vi.fn>;
    disconnect: ReturnType<typeof vi.fn>;
  };

  beforeEach(async () => {
    const usersFacadeStub = {
      users: signal([] as Array<{ id: string; username: string; isInCall: boolean; email?: string }>),
      updateUserList: vi.fn(),
    };

    signalrStub = {
      connectionId: 'self-connection',
      setCallbacks: vi.fn(),
      attachSignalRHandlers: vi.fn(),
      connectAndJoin: vi.fn().mockResolvedValue(undefined),
      inviteToMeeting: vi.fn().mockResolvedValue(undefined),
      setInCall: vi.fn().mockResolvedValue(undefined),
      setLeftCall: vi.fn().mockResolvedValue(undefined),
      sendChatMessage: vi.fn().mockResolvedValue(undefined),
      disconnect: vi.fn().mockResolvedValue(undefined),
    };

    const livekitStub = {
      remoteParticipants: signal([]),
      localParticipant: signal(null),
      currentMeetingId: signal<string | null>(null),
      currentMeetingTitle: signal(''),
      isRecording: signal(false),
      cameraEnabled: signal(false),
      micEnabled: signal(false),
      localTrackVersion: signal(0),
      activeSpeakerIds: signal<ReadonlySet<string>>(new Set()),
      joinMeeting: vi.fn().mockResolvedValue(undefined),
      leaveMeeting: vi.fn().mockResolvedValue(undefined),
    };

    const meetingApiStub = {
      create: vi.fn(),
      join: vi.fn(),
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
        { provide: SignalrService, useValue: signalrStub },
        { provide: LivekitMeetingService, useValue: livekitStub },
        { provide: MeetingApiService, useValue: meetingApiStub },
        { provide: AuthService, useValue: authServiceStub },
        { provide: UserDirectoryService, useValue: userDirectoryStub },
        { provide: Router, useValue: routerStub },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { data: { mode: 'call' }, paramMap: { get: () => null } },
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MeetupHome);
    component = fixture.componentInstance;
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('wires SignalR callback handlers and joins the hub on init', async () => {
    await component.ngOnInit();

    expect(signalrStub.setCallbacks).toHaveBeenCalledTimes(1);
    const callbacks = signalrStub.setCallbacks.mock.calls[0][0];
    expect(typeof callbacks.onIncomingInvite).toBe('function');
    expect(typeof callbacks.onInviteDeclined).toBe('function');
    expect(typeof callbacks.onInviteAccepted).toBe('function');
    expect(typeof callbacks.onCallFailed).toBe('function');

    expect(signalrStub.attachSignalRHandlers).toHaveBeenCalledTimes(1);
    expect(signalrStub.connectAndJoin).toHaveBeenCalledTimes(1);
  });
});
