import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthService } from './auth.service';
import { IncomingCallRingMs, IncomingCallService } from './incoming-call.service';
import { RingtoneService } from './ringtone.service';
import { InviteCancelledPayload, InvitePayload, SignalrService } from './signalr.service';
import { ToastService } from './toast.service';

describe('IncomingCallService', () => {
  let service: IncomingCallService;
  let incoming$: Subject<InvitePayload>;
  let cancelled$: Subject<InviteCancelledPayload>;
  let signalr: Record<string, any>;
  let ringtone: { start: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn> };
  let toast: { info: ReturnType<typeof vi.fn> };
  let router: { navigate: ReturnType<typeof vi.fn> };

  const invite = (id: string, from = 'alice'): InvitePayload => ({
    inviteId: id,
    meetingId: `meeting-${id}`,
    fromUserId: `${from}-connection`,
    fromUsername: from,
  });

  beforeEach(() => {
    vi.useFakeTimers();
    incoming$ = new Subject();
    cancelled$ = new Subject();
    signalr = {
      incomingInvites$: incoming$,
      inviteCancelled$: cancelled$,
      closed$: new Subject<void>(),
      isConnected: true,
      attachSignalRHandlers: vi.fn(),
      connectAndJoin: vi.fn().mockResolvedValue(undefined),
      disconnect: vi.fn().mockResolvedValue(undefined),
      respondToInvite: vi.fn().mockResolvedValue(true),
      missInvite: vi.fn().mockResolvedValue(undefined),
    };
    ringtone = { start: vi.fn(), stop: vi.fn() };
    toast = { info: vi.fn() };
    router = { navigate: vi.fn().mockResolvedValue(true) };

    TestBed.configureTestingModule({
      providers: [
        { provide: SignalrService, useValue: signalr },
        { provide: AuthService, useValue: { isAuthenticated: signal(true) } },
        { provide: RingtoneService, useValue: ringtone },
        { provide: ToastService, useValue: toast },
        { provide: Router, useValue: router },
      ],
    });
    service = TestBed.inject(IncomingCallService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('rings when a call comes in', () => {
    incoming$.next(invite('a'));

    expect(service.incoming()?.inviteId).toBe('a');
    expect(ringtone.start).toHaveBeenCalledTimes(1);
  });

  it('stops ringing when answered, then opens the meeting', async () => {
    incoming$.next(invite('a'));

    await service.accept();

    expect(ringtone.stop).toHaveBeenCalled();
    expect(signalr['respondToInvite']).toHaveBeenCalledWith('a', true);
    expect(service.incoming()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/meet', 'meeting-a']);
  });

  it('does not open a call the caller already hung up on', async () => {
    signalr['respondToInvite'].mockResolvedValue(false);
    incoming$.next(invite('a'));

    await service.accept();

    expect(router.navigate).not.toHaveBeenCalled();
    expect(toast.info).toHaveBeenCalledWith('alice is no longer calling.');
  });

  it('stops ringing when declined', async () => {
    incoming$.next(invite('a'));

    await service.decline();

    expect(ringtone.stop).toHaveBeenCalled();
    expect(signalr['respondToInvite']).toHaveBeenCalledWith('a', false);
    expect(service.incoming()).toBeNull();
  });

  it('stops ringing when the caller hangs up', () => {
    incoming$.next(invite('a'));

    cancelled$.next({ inviteId: 'a', meetingId: 'meeting-a' });

    expect(ringtone.stop).toHaveBeenCalled();
    expect(service.incoming()).toBeNull();
    expect(toast.info).toHaveBeenCalledWith('Missed call from alice.');
  });

  it('rings out after a while and reports the call as missed', () => {
    incoming$.next(invite('a'));

    vi.advanceTimersByTime(IncomingCallRingMs);

    expect(ringtone.stop).toHaveBeenCalled();
    expect(service.incoming()).toBeNull();
    expect(signalr['missInvite']).toHaveBeenCalledWith('a');
  });

  it('keeps the first call ringing and reports a second caller as missed', () => {
    incoming$.next(invite('a', 'alice'));
    incoming$.next(invite('b', 'bob'));

    expect(service.incoming()?.inviteId).toBe('a');
    expect(signalr['missInvite']).toHaveBeenCalledWith('b');
    expect(toast.info).toHaveBeenCalledWith('Missed call from bob.');
  });
});
