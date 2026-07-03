import { inject, Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { UsersFacade } from '../store/facades/users.facade';
import { CallOfferDto } from '../dtos/callofferDto';
import { CallFacade } from '../store/facades/call.facade';
import { AuthService } from './auth.service';

type IncomingCallPayload = {
  inviteId: string;
  roomId: string;
  fromUserId: string;
  fromUsername: string;
};

type CallDeclinedPayload = {
  inviteId: string;
  roomId: string;
  declinedByUserId: string;
  declinedByUsername: string;
};

type CallAcceptedPayload = {
  inviteId: string;
  roomId: string;
  acceptedByUserId: string;
  acceptedByUsername: string;
  users: Array<{ id: string; username: string; isInCall: boolean; roomId?: string | null }>;
};

type RoomParticipantsPayload = {
  roomId: string;
  users: Array<{ id: string; username: string; isInCall: boolean; roomId?: string | null }>;
};

type CandidatePayload = {
  roomId: string;
  from: string;
  to: string;
  candidate: RTCIceCandidateInit;
};

type MediaStatePayload = {
  roomId: string;
  userId: string;
  isCameraOn: boolean;
  isMicOn: boolean;
};

type SignalRCallbacks = {
  onIncomingCall?: (payload: IncomingCallPayload) => void;
  onCallDeclined?: (payload: CallDeclinedPayload) => void;
  onCallAccepted?: (payload: CallAcceptedPayload) => void;
  onInstantMeetingStarted?: (payload: RoomParticipantsPayload) => void;
  onCallFailed?: (message: string) => void;
  onCallRinging?: (payload: { toUsername: string }) => void;
  onRoomParticipantsUpdated?: (payload: RoomParticipantsPayload) => void;
  onReceiveCallOffer?: (offer: CallOfferDto) => void;
  onReceiveCallAnswer?: (answer: CallOfferDto) => void;
  onReceiveCandidate?: (candidatePayload: CandidatePayload) => void;
  onMediaStateUpdated?: (payload: MediaStatePayload) => void;
};

@Injectable()
export class SignalrService {
  private userFacade = inject(UsersFacade);
  private callFacade = inject(CallFacade);
  private authService = inject(AuthService);
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

  private createHubConnection() {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl('https://localhost:7248/callHub', {
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

  async connectAndJoin() {
    if (!this.authService.isAuthenticated()) {
      return;
    }

    if (this.hubConnection.state === signalR.HubConnectionState.Disconnected) {
      await this.hubConnection.start();
      this.myConnectionID = this.hubConnection.connectionId ?? '';
      this.hasJoinedCurrentConnection = false;
      console.log('SignalR Connected with ID: ', this.myConnectionID);
    }

    await this.joinUser();
  }

  async disconnect() {
    if (this.hubConnection.state !== signalR.HubConnectionState.Disconnected) {
      await this.hubConnection.stop();
    }

    this.myConnectionID = '';
    this.hasJoinedCurrentConnection = false;
    this.userFacade.updateUserList([]);
  }

  async joinUser() {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected || this.hasJoinedCurrentConnection) {
      return;
    }

    await this.hubConnection.invoke('JoinUser');
    this.hasJoinedCurrentConnection = true;
  }

  async startCall(targetUserId: string): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('StartCall', targetUserId);
  }

  async respondToCall(inviteId: string, accepted: boolean): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('RespondToCall', inviteId, accepted);
  }

  async startInstantMeeting(): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('StartInstantMeeting');
  }

  async sendCallOffer(roomId: string, toUserId: string, offer: RTCSessionDescriptionInit): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('SendCallOffer', new CallOfferDto(this.myConnectionID, toUserId, roomId, offer));
  }

  async sendCallAnswer(roomId: string, toUserId: string, answer: RTCSessionDescriptionInit): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('SendCallAnswer', new CallOfferDto(this.myConnectionID, toUserId, roomId, answer));
  }

  async sendIceCandidate(roomId: string, toUserId: string, candidate: RTCIceCandidateInit): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('SendCandidate', roomId, toUserId, candidate);
  }

  async sendMediaState(roomId: string, isCameraOn: boolean, isMicOn: boolean): Promise<void> {
    if (this.hubConnection.state !== signalR.HubConnectionState.Connected) {
      return;
    }

    await this.hubConnection.invoke('UpdateMediaState', roomId, isCameraOn, isMicOn);
  }

  async leaveCall(): Promise<void> {
    if (this.hubConnection.state === signalR.HubConnectionState.Connected) {
      await this.hubConnection.invoke('LeaveCall');
    }

    this.callFacade.updateCallState(false);
  }

  setCallbacks(callbacks: SignalRCallbacks) {
    this.callbacks = callbacks;
  }

  private userJoined() {
    this.hubConnection.on('UserJoined', (allUsers: Array<{ id: string; username: string; isInCall: boolean; roomId?: string | null }>) => {
      console.log('User joined from server: ', JSON.stringify(allUsers));
      this.userFacade.updateUserList(allUsers);
    });
  }

  private receiveIncomingCall() {
    this.hubConnection.on('ReceiveIncomingCall', (payload: IncomingCallPayload) => {
      this.callbacks.onIncomingCall?.(payload);
    });
  }

  private receiveCallDeclined() {
    this.hubConnection.on('CallDeclined', (payload: CallDeclinedPayload) => {
      this.callbacks.onCallDeclined?.(payload);
    });
  }

  private receiveCallAccepted() {
    this.hubConnection.on('CallAccepted', (payload: CallAcceptedPayload) => {
      this.callFacade.updateCallState(true);
      this.callbacks.onCallAccepted?.(payload);
    });
  }

  private receiveCallRinging() {
    this.hubConnection.on('CallRinging', (payload: { toUsername: string }) => {
      this.callbacks.onCallRinging?.(payload);
    });
  }

  private receiveCallFailed() {
    this.hubConnection.on('CallFailed', (message: string) => {
      this.callbacks.onCallFailed?.(message);
    });
  }

  private receiveParticipantsUpdated() {
    this.hubConnection.on('RoomParticipantsUpdated', (payload: RoomParticipantsPayload) => {
      this.callbacks.onRoomParticipantsUpdated?.(payload);
    });
  }

  private receiveInstantMeetingStarted() {
    this.hubConnection.on('InstantMeetingStarted', (payload: RoomParticipantsPayload) => {
      this.callFacade.updateCallState(true);
      this.callbacks.onInstantMeetingStarted?.(payload);
    });
  }

  private receiveCallOffer() {
    this.hubConnection.on('ReceiveCallOffer', (callOffer: CallOfferDto) => {
      this.callbacks.onReceiveCallOffer?.(callOffer);
    });
  }

  private receiveCallAnswer() {
    this.hubConnection.on('ReceiveCallAnswer', (callOffer: CallOfferDto) => {
      this.callbacks.onReceiveCallAnswer?.(callOffer);
    });
  }

  private receiveCandidate() {
    this.hubConnection.on('ReceiveCandidate', (payload: CandidatePayload) => {
      this.callbacks.onReceiveCandidate?.(payload);
    });
  }

  private receiveMediaStateUpdated() {
    this.hubConnection.on('MediaStateUpdated', (payload: MediaStatePayload) => {
      this.callbacks.onMediaStateUpdated?.(payload);
    });
  }

  attachSignalRHandlers() {
    if (this.handlersAttached) {
      return;
    }

    this.userJoined();
    this.receiveIncomingCall();
    this.receiveCallDeclined();
    this.receiveCallAccepted();
    this.receiveCallRinging();
    this.receiveCallFailed();
    this.receiveParticipantsUpdated();
    this.receiveInstantMeetingStarted();
    this.receiveCallOffer();
    this.receiveCallAnswer();
    this.receiveCandidate();
    this.receiveMediaStateUpdated();
    this.handlersAttached = true;
  }
}
