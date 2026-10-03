import { effect, inject, Injectable, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { AuthService } from './auth.service';
import { RingtoneService } from './ringtone.service';
import { InvitePayload, SignalrService } from './signalr.service';
import { ToastService } from './toast.service';

/** How long a call rings before it counts as missed — the same as the caller's wait. */
export const IncomingCallRingMs = 45_000;

/** Wait before retrying a connection that failed or closed, while still signed in. */
const ReconnectDelayMs = 5_000;

/**
 * Everything about a call ringing on this device: keeps the user reachable on every page,
 * rings, and answers or declines. The popup (IncomingCallDialog) is only its view.
 *
 * The connection lives here rather than on individual pages because a user who was not on the
 * dashboard or the call page used to be offline to everyone — and a call to them never arrived.
 * The root component injects this once, which starts it.
 */
@Injectable({ providedIn: 'root' })
export class IncomingCallService {
  private readonly signalR = inject(SignalrService);
  private readonly auth = inject(AuthService);
  private readonly ringtone = inject(RingtoneService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  /** The call ringing right now, if any. */
  readonly incoming = signal<InvitePayload | null>(null);

  /** True from pressing Accept until the meeting page takes over. */
  readonly answering = signal(false);

  private ringTimer: ReturnType<typeof setTimeout> | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private originalTitle = '';

  constructor() {
    this.signalR.attachSignalRHandlers();

    this.signalR.incomingInvites$
      .pipe(takeUntilDestroyed())
      .subscribe((invite) => this.onInvite(invite));

    this.signalR.inviteCancelled$.pipe(takeUntilDestroyed()).subscribe(({ inviteId }) => {
      const current = this.incoming();
      if (current?.inviteId === inviteId) {
        this.dismiss();
        this.toast.info(`Missed call from ${current.fromUsername}.`);
      }
    });

    this.signalR.closed$
      .pipe(takeUntilDestroyed())
      .subscribe(() => this.scheduleReconnect());

    // Online while signed in, offline once signed out.
    effect(() => {
      const signedIn = this.auth.isAuthenticated();
      untracked(() => {
        if (signedIn) {
          void this.connect();
        } else {
          this.clearReconnect();
          this.dismiss();
          void this.signalR.disconnect();
        }
      });
    });
  }

  async accept(): Promise<void> {
    const invite = this.incoming();
    if (!invite || this.answering()) {
      return;
    }

    this.answering.set(true);
    this.stopRinging();

    let stillThere = false;
    try {
      stillThere = await this.signalR.respondToInvite(invite.inviteId, true);
    } catch (err) {
      console.error('Accepting the call failed', err);
    }

    this.incoming.set(null);
    this.answering.set(false);

    if (!stillThere) {
      this.toast.info(`${invite.fromUsername} is no longer calling.`);
      return;
    }

    // The meeting page joins the room, leaving any call this user was already in.
    await this.router.navigate(['/meet', invite.meetingId]);
  }

  async decline(): Promise<void> {
    const invite = this.incoming();
    if (!invite) {
      return;
    }

    this.dismiss();
    try {
      await this.signalR.respondToInvite(invite.inviteId, false);
    } catch (err) {
      console.error('Declining the call failed', err);
    }
  }

  private onInvite(invite: InvitePayload): void {
    if (this.incoming()) {
      // Already ringing for someone else: like call waiting with no second line, the newer call
      // is reported back to its caller as missed and the user is told who tried.
      void this.signalR.missInvite(invite.inviteId).catch(() => undefined);
      this.toast.info(`Missed call from ${invite.fromUsername}.`);
      return;
    }

    this.incoming.set(invite);
    this.ringtone.start();
    this.flagTitle(invite.fromUsername);

    this.ringTimer = setTimeout(() => {
      if (this.incoming()?.inviteId !== invite.inviteId) {
        return;
      }
      this.dismiss();
      this.toast.info(`Missed call from ${invite.fromUsername}.`);
      void this.signalR.missInvite(invite.inviteId).catch(() => undefined);
    }, IncomingCallRingMs);
  }

  /** Hides the popup and stops ringing, without telling the caller anything. */
  private dismiss(): void {
    this.stopRinging();
    this.incoming.set(null);
    this.answering.set(false);
  }

  private stopRinging(): void {
    if (this.ringTimer !== null) {
      clearTimeout(this.ringTimer);
      this.ringTimer = null;
    }
    this.ringtone.stop();
    this.restoreTitle();
  }

  /** Shows the call in the tab title too, for when the app is in a background tab. */
  private flagTitle(caller: string): void {
    if (typeof document === 'undefined') {
      return;
    }
    this.originalTitle = document.title;
    document.title = `Incoming call from ${caller}`;
  }

  private restoreTitle(): void {
    if (typeof document !== 'undefined' && this.originalTitle) {
      document.title = this.originalTitle;
      this.originalTitle = '';
    }
  }

  private async connect(): Promise<void> {
    this.clearReconnect();
    try {
      await this.signalR.connectAndJoin();
    } catch (err) {
      console.warn('Could not connect for calls; retrying shortly', err);
      this.scheduleReconnect();
    }
  }

  private scheduleReconnect(): void {
    if (!this.auth.isAuthenticated() || this.reconnectTimer !== null) {
      return;
    }
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      if (this.auth.isAuthenticated() && !this.signalR.isConnected) {
        void this.connect();
      }
    }, ReconnectDelayMs);
  }

  private clearReconnect(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }
}
