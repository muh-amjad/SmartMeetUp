import { inject, Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
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

export type SignalRCallbacks = {
  onIncomingInvite?: (payload: InvitePayload) => void;
  onInviteRinging?: (payload: InviteRingingPayload) => void;
  onInviteDeclined?: (payload: InviteDeclinedPayload) => void;
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

  async connectAndJoin(): Promise<void> {
    if (!this.authService.isAuthenticated()) {
      return;
    }

    if (this.hubConnection.state === signalR.HubConnectionState.Disconnected) {
      await this.hubConnection.start();
      this.myConnectionID = this.hubConnection.connectionId ?? '';
      this.hasJoinedCurrentConnection = false;
      console.log('SignalR Connected with ID:', this.myConnectionID);
    }

    await this.joinUser();
  }

  async disconnect(): Promise<void> {
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

  /** Accept or decline an invite. */
  async respondToInvite(inviteId: string, accepted: boolean): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('RespondToInvite', inviteId, accepted);
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
      this.callbacks.onIncomingInvite?.(payload);
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
