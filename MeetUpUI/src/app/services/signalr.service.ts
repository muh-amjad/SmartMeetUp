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

export type InviteAcceptedPayload = {
  inviteId: string;
  meetingId: string;
  acceptedByUserId: string;
  acceptedByUsername: string;
};

export type SignalRCallbacks = {
  onIncomingInvite?: (payload: InvitePayload) => void;
  onInviteRinging?: (payload: InviteRingingPayload) => void;
  onInviteDeclined?: (payload: InviteDeclinedPayload) => void;
  onInviteAccepted?: (payload: InviteAcceptedPayload) => void;
  onCallFailed?: (message: string) => void;
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

    this.hubConnection.onreconnected(() => {
      this.myConnectionID = this.hubConnection.connectionId ?? '';
      this.hasJoinedCurrentConnection = false;
      console.log('[SignalR] Reconnected');
      void this.joinUser();
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
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    await this.hubConnection.invoke('SetInCall', meetingId);
  }

  async setLeftCall(): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }
    await this.hubConnection.invoke('SetLeftCall');
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

    this.handlersAttached = true;
  }
}
