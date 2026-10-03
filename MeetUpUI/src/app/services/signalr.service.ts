import { inject, Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { UsersFacade } from '../store/facades/users.facade';
import { AuthService } from './auth.service';
import { environment } from '../../environments/environment';

/**
 * Payload types that mirror the C# MeetingHub events.
 */
export type InvitePayload = {
  inviteId: string;
  meetingId: string;
  fromUserId: string;
  fromUsername: string;
};

export type InviteRingingPayload = {
  inviteId: string;
  meetingId: string;
  toUserId: string;
  toUsername: string;
};

/** The caller hung up (or left, or dropped) before the invite was answered. */
export type InviteCancelledPayload = {
  inviteId: string;
  meetingId: string;
};

/** The callee let the call ring out, or went offline while it was ringing. */
export type InviteMissedPayload = {
  inviteId: string;
  meetingId: string;
  missedByUserId: string;
  missedByUsername: string;
};

export type InviteDeclinedPayload = {
  inviteId: string;
  meetingId: string;
  declinedByUserId: string;
  declinedByUsername: string;
};

export type ChatMessageReceivedPayload = {
  id: string;
  meetingId: string;
  senderUserId: string;
  senderUsername: string;
  text: string;
  sentUtc: string;
};

export type InviteAcceptedPayload = {
  inviteId: string;
  meetingId: string;
  acceptedByUserId: string;
  acceptedByUsername: string;
};

/** A completed speaking turn, matching the server's SpeakingIntervalDto. */
export type SpeakingInterval = {
  startedUtc: string;
  stoppedUtc: string;
};

/**
 * Events for whichever page is showing. Incoming invites are deliberately not here: they are
 * app-wide (see `incomingInvites$`), so a call can ring on any page, not only on the two that
 * used to register for it.
 */
export type SignalRCallbacks = {
  onInviteRinging?: (payload: InviteRingingPayload) => void;
  onInviteDeclined?: (payload: InviteDeclinedPayload) => void;
  onInviteMissed?: (payload: InviteMissedPayload) => void;
  onInviteAccepted?: (payload: InviteAcceptedPayload) => void;
  onCallFailed?: (message: string) => void;
  onChatMessageReceived?: (payload: ChatMessageReceivedPayload) => void;
};

/**
 * Wraps SignalR connection to /meetingHub. Post-Phase-1, this hub is only
 * responsible for presence and invite delivery. All media (SDP/ICE/tracks)
 * is handled by LiveKit via LivekitMeetingService.
 */
@Injectable()
export class SignalrService {
  private readonly userFacade = inject(UsersFacade);
  private readonly authService = inject(AuthService);

  private myConnectionID = '';
  private hubConnection!: signalR.HubConnection;
  private handlersAttached = false;
  private hasJoinedCurrentConnection = false;
  private callbacks: SignalRCallbacks = {};

  /** The connection attempt in flight, shared so overlapping callers don't start it twice. */
  private connecting: Promise<void> | null = null;

  /** Someone is calling this user. App-wide — the incoming-call popup listens to it. */
  readonly incomingInvites$ = new Subject<InvitePayload>();

  /** A call that was ringing here was withdrawn by its caller before it was answered. */
  readonly inviteCancelled$ = new Subject<InviteCancelledPayload>();

  /** The connection closed for good (automatic reconnect gave up, or it was stopped). */
  readonly closed$ = new Subject<void>();

  /** The meeting this client last told the hub it is in, so a reconnect can restore it. */
  private activeMeetingId: string | null = null;

  constructor() {
    this.createHubConnection();
  }

  get connectionId(): string {
    return this.myConnectionID;
  }

  private createHubConnection(): void {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(environment.signalrHubUrl, {
        accessTokenFactory: () => this.authService.token() ?? '',
      })
      .withAutomaticReconnect()
      .build();

    this.hubConnection.onclose(() => {
      this.myConnectionID = '';
      this.hasJoinedCurrentConnection = false;
      this.closed$.next();
    });

    this.hubConnection.onreconnected(async () => {
      this.myConnectionID = this.hubConnection.connectionId ?? '';
      this.hasJoinedCurrentConnection = false;
      console.log('[SignalR] Reconnected');
      await this.joinUser();

      // A reconnect arrives as a brand-new connection, which the hub registers as idle and in no
      // meeting group. Without re-announcing the meeting, everyone else sees this user as free
      // to call, and the chat this user sends from here on is rejected while chat sent to them
      // never arrives — for the rest of the call. Reconnects happen on every network blip and
      // every API restart (dotnet watch restarts on each code change).
      if (this.activeMeetingId) {
        await this.setInCall(this.activeMeetingId);
      }
    });
  }

  /**
   * Connects (if needed) and registers this user as online. Safe to call from several places at
   * once: the app connects as soon as someone signs in, and the call page awaits the same
   * attempt rather than racing a second start() — which SignalR rejects outright.
   */
  connectAndJoin(): Promise<void> {
    if (!this.authService.isAuthenticated()) {
      return Promise.resolve();
    }

    this.connecting ??= this.startAndJoin().finally(() => {
      this.connecting = null;
    });
    return this.connecting;
  }

  get isConnected(): boolean {
    return this.hubConnection.state === signalR.HubConnectionState.Connected;
  }

  private async startAndJoin(): Promise<void> {
    if (this.hubConnection.state === signalR.HubConnectionState.Disconnected) {
      await this.hubConnection.start();
      this.myConnectionID = this.hubConnection.connectionId ?? '';
      this.hasJoinedCurrentConnection = false;
      console.log('SignalR Connected with ID:', this.myConnectionID);
    }

    await this.joinUser();
  }

  async disconnect(): Promise<void> {
    this.activeMeetingId = null;
    if (this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      await this.hubConnection.stop();
    }

    this.myConnectionID = '';
    this.hasJoinedCurrentConnection = false;
    this.userFacade.updateUserList([]);
  }

  async joinUser(): Promise<void> {
    if (
      this.hubConnection.state !== signalR.HubConnectionState.Connected ||
      this.hasJoinedCurrentConnection
    ) {
      return;
    }

    await this.hubConnection.invoke('JoinUser');
    this.hasJoinedCurrentConnection = true;
  }

  /**
   * Send a meeting invite to another user.
   * The caller has already created the meeting via POST /api/meetings.
   */
  async inviteToMeeting(targetConnectionId: string, meetingId: string): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('InviteToMeeting', targetConnectionId, meetingId);
  }

  /**
   * Accept or decline an invite. Resolves false when the call no longer stands — the caller hung
   * up a moment before — so an accept doesn't walk into an empty room.
   */
  async respondToInvite(inviteId: string, accepted: boolean): Promise<boolean> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return false;
    }

    return await this.hubConnection.invoke<boolean>('RespondToInvite', inviteId, accepted);
  }

  /** Report that an invite rang out unanswered, so the caller hears it was missed. */
  async missInvite(inviteId: string): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('MissInvite', inviteId);
  }

  setCallbacks(callbacks: SignalRCallbacks): void {
    this.callbacks = callbacks;
  }

  async setInCall(meetingId: string): Promise<void> {
    // Remembered even while disconnected, so the reconnect handler can announce it once back.
    this.activeMeetingId = meetingId;
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    await this.hubConnection.invoke('SetInCall', meetingId);
  }

  async setLeftCall(): Promise<void> {
    this.activeMeetingId = null;
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    await this.hubConnection.invoke('SetLeftCall');
  }

  /** Send a chat message to the current meeting (hub persists + broadcasts). */
  async sendChatMessage(meetingId: string, text: string): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    await this.hubConnection.invoke('SendChatMessage', meetingId, text);
  }

  /**
   * Report stretches during which this client was an active speaker. LiveKit only surfaces
   * active-speaker changes to connected SDKs — its server webhooks carry no speaker data — so the
   * browser is the only place these events exist. The server attributes them to the authenticated
   * caller, so no user id is sent.
   */
  async reportSpeakingIntervals(
    meetingId: string,
    intervals: SpeakingInterval[],
  ): Promise<void> {
    if (intervals.length === 0) {
      return;
    }
    // Rejects rather than returning quietly, so the caller keeps the batch and retries it. A
    // silent return here used to discard every turn spoken while the connection was down.
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      throw new Error('SignalR is not connected; speaking intervals not sent.');
    }
    await this.hubConnection.invoke('ReportSpeakingIntervals', meetingId, intervals);
  }

  attachSignalRHandlers(): void {
    if (this.handlersAttached) {
      return;
    }

    this.hubConnection.on(
      'UserJoined',
      (
        allUsers: Array<{
          id: string;
          username: string;
          isInCall: boolean;
          roomId?: string | null;
        }>,
      ) => {
        this.userFacade.updateUserList(allUsers);
      },
    );

    this.hubConnection.on('ReceiveInvite', (payload: InvitePayload) => {
      this.incomingInvites$.next(payload);
    });

    this.hubConnection.on('InviteCancelled', (payload: InviteCancelledPayload) => {
      this.inviteCancelled$.next(payload);
    });

    this.hubConnection.on('InviteMissed', (payload: InviteMissedPayload) => {
      this.callbacks.onInviteMissed?.(payload);
    });

    this.hubConnection.on('InviteRinging', (payload: InviteRingingPayload) => {
      this.callbacks.onInviteRinging?.(payload);
    });

    this.hubConnection.on('InviteAccepted', (payload: InviteAcceptedPayload) => {
      this.callbacks.onInviteAccepted?.(payload);
    });

    this.hubConnection.on('InviteDeclined', (payload: InviteDeclinedPayload) => {
      this.callbacks.onInviteDeclined?.(payload);
    });

    this.hubConnection.on('CallFailed', (message: string) => {
      this.callbacks.onCallFailed?.(message);
    });

    this.hubConnection.on('ChatMessageReceived', (payload: ChatMessageReceivedPayload) => {
      this.callbacks.onChatMessageReceived?.(payload);
    });

    this.handlersAttached = true;
  }
}
