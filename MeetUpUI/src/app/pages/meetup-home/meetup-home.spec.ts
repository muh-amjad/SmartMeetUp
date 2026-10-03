import { Location } from '@angular/common';
import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, ParamMap, Router } from '@angular/router';
import { BehaviorSubject, of } from 'rxjs';
import { vi } from 'vitest';
import { AuthService } from '../../services/auth.service';
import { LivekitMeetingService } from '../../services/livekit-meeting.service';
import { MeetingApiService } from '../../services/meeting-api.service';
import { SignalrService } from '../../services/signalr.service';
import { UsersFacade } from '../../store/facades/users.facade';
import { MeetupHome } from './meetup-home';

describe('MeetupHome', () => {
  let component: MeetupHome;
  let fixture: ComponentFixture<MeetupHome>;
  let paramMap: BehaviorSubject<ParamMap>;
  let signalrStub: Record<string, any>;
  let livekitStub: Record<string, any>;
  let meetingApiStub: Record<string, any>;
  let routerStub: { navigate: ReturnType<typeof vi.fn> };
  let locationStub: { replaceState: ReturnType<typeof vi.fn> };

  /** Lets the async work kicked off by ngOnInit run to completion. */
  const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

  beforeEach(async () => {
    paramMap = new BehaviorSubject(convertToParamMap({}));
    history.replaceState(null, '');

    signalrStub = {
      connectionId: 'self-connection',
      setCallbacks: vi.fn(),
      connectAndJoin: vi.fn().mockResolvedValue(undefined),
      inviteToMeeting: vi.fn().mockResolvedValue(undefined),
      setInCall: vi.fn().mockResolvedValue(undefined),
      setLeftCall: vi.fn().mockResolvedValue(undefined),
      sendChatMessage: vi.fn().mockResolvedValue(undefined),
    };

    const currentMeetingId = signal<string | null>(null);
    livekitStub = {
      remoteParticipants: signal([]),
      localParticipant: signal(null),
      currentMeetingId,
      currentMeetingTitle: signal(''),
      isRecording: signal(false),
      cameraEnabled: signal(false),
      micEnabled: signal(false),
      localTrackVersion: signal(0),
      activeSpeakerIds: signal<ReadonlySet<string>>(new Set()),
      joinMeeting: vi.fn().mockImplementation(async (id: string) => currentMeetingId.set(id)),
      leaveMeeting: vi.fn().mockImplementation(async () => currentMeetingId.set(null)),
    };

    meetingApiStub = {
      create: vi.fn().mockReturnValue(of({ meetingId: 'new-meeting' })),
      getChat: vi.fn().mockReturnValue(of([])),
    };

    routerStub = { navigate: vi.fn().mockResolvedValue(true) };
    locationStub = { replaceState: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [MeetupHome],
      providers: [
        { provide: UsersFacade, useValue: { users: signal([]) } },
        { provide: SignalrService, useValue: signalrStub },
        { provide: LivekitMeetingService, useValue: livekitStub },
        { provide: MeetingApiService, useValue: meetingApiStub },
        {
          provide: AuthService,
          useValue: { currentUser: () => ({ username: 'tester', email: 'tester@meetup.test' }) },
        },
        { provide: Router, useValue: routerStub },
        { provide: Location, useValue: locationStub },
        { provide: ActivatedRoute, useValue: { paramMap } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(MeetupHome);
    component = fixture.componentInstance;
  });

  it('wires its call callbacks and waits for the shared connection', async () => {
    fixture.detectChanges();
    await settle();

    expect(signalrStub['setCallbacks']).toHaveBeenCalledTimes(1);
    const callbacks = signalrStub['setCallbacks'].mock.calls[0][0];
    expect(callbacks.onIncomingInvite).toBeUndefined();
    expect(typeof callbacks.onInviteDeclined).toBe('function');
    expect(typeof callbacks.onInviteMissed).toBe('function');
    expect(typeof callbacks.onInviteAccepted).toBe('function');
    expect(typeof callbacks.onCallFailed).toBe('function');
    expect(signalrStub['connectAndJoin']).toHaveBeenCalledTimes(1);
  });

  it('has no lobby: /meet with nothing to start goes back to the dashboard', async () => {
    fixture.detectChanges();
    await settle();

    expect(routerStub.navigate).toHaveBeenCalledWith(['/dashboard'], { replaceUrl: true });
    expect(livekitStub['joinMeeting']).not.toHaveBeenCalled();
  });

  it('joins the meeting named in the URL', async () => {
    paramMap.next(convertToParamMap({ meetingId: 'abc' }));

    fixture.detectChanges();
    await settle();

    expect(livekitStub['joinMeeting']).toHaveBeenCalledWith('abc');
    expect(signalrStub['setInCall']).toHaveBeenCalledWith('abc');
    expect(component.inCall()).toBe(true);
  });

  it('switches meetings when an accepted invite changes the URL', async () => {
    paramMap.next(convertToParamMap({ meetingId: 'first' }));
    fixture.detectChanges();
    await settle();

    paramMap.next(convertToParamMap({ meetingId: 'second' }));
    await settle();

    expect(livekitStub['joinMeeting']).toHaveBeenLastCalledWith('second');
    expect(signalrStub['setInCall']).toHaveBeenLastCalledWith('second');
  });

  it('starts a new meeting and rings the person called from the dashboard', async () => {
    history.replaceState(
      { source: 'call', invitee: { connectionId: 'bob-connection', username: 'bob' } },
      '',
    );

    fixture.detectChanges();
    await settle();

    expect(meetingApiStub['create']).toHaveBeenCalledTimes(1);
    expect(locationStub.replaceState).toHaveBeenCalledWith('/meet/new-meeting');
    expect(livekitStub['joinMeeting']).toHaveBeenCalledWith('new-meeting');
    expect(signalrStub['inviteToMeeting']).toHaveBeenCalledWith('bob-connection', 'new-meeting');
    expect(component.ringingCallee()?.username).toBe('bob');
  });

  it('hangs up and returns to the dashboard when the only person called declines', async () => {
    history.replaceState(
      { source: 'call', invitee: { connectionId: 'bob-connection', username: 'bob' } },
      '',
    );
    fixture.detectChanges();
    await settle();

    const callbacks = signalrStub['setCallbacks'].mock.calls[0][0];
    callbacks.onInviteDeclined({
      inviteId: 'i1',
      meetingId: 'new-meeting',
      declinedByUserId: 'bob-connection',
      declinedByUsername: 'bob',
    });
    await settle();

    expect(livekitStub['leaveMeeting']).toHaveBeenCalled();
    expect(signalrStub['setLeftCall']).toHaveBeenCalled();
    expect(routerStub.navigate).toHaveBeenCalledWith(['/dashboard'], { replaceUrl: true });
  });

  it('stays in a call that is already going when an added person declines', async () => {
    paramMap.next(convertToParamMap({ meetingId: 'abc' }));
    fixture.detectChanges();
    await settle();

    await component.addToCall({ id: 'carol-connection', username: 'carol' } as never);
    const callbacks = signalrStub['setCallbacks'].mock.calls[0][0];
    callbacks.onInviteDeclined({
      inviteId: 'i2',
      meetingId: 'abc',
      declinedByUserId: 'carol-connection',
      declinedByUsername: 'carol',
    });
    await settle();

    expect(livekitStub['leaveMeeting']).not.toHaveBeenCalled();
    expect(component.inCall()).toBe(true);
  });
});
