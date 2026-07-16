import { inject, Injectable, signal } from '@angular/core';
import {
  ConnectionState,
  LocalParticipant,
  RemoteParticipant,
  Room,
  RoomEvent,
  Track,
} from 'livekit-client';
import { firstValueFrom } from 'rxjs';
import { MeetingApiService } from './meeting-api.service';

/**
 * Wraps a LiveKit Room object and exposes reactive signals for the UI.
 * Replaces the mesh WebRTC logic that WebrtcPeerService used to handle in Phase 0.
 */
@Injectable({ providedIn: 'root' })
export class LivekitMeetingService {
  private readonly meetingApi = inject(MeetingApiService);

  private room: Room | null = null;

  // ── Public reactive state ────────────────────────────────
  readonly connectionState = signal<ConnectionState>(ConnectionState.Disconnected);
  readonly localParticipant = signal<LocalParticipant | null>(null);
  readonly remoteParticipants = signal<RemoteParticipant[]>([]);
  readonly isRecording = signal<boolean>(false);
  readonly currentMeetingId = signal<string | null>(null);
  readonly currentMeetingTitle = signal<string>('');
  readonly isHost = signal<boolean>(false);

  /**
   * Join a meeting: fetches a fresh token from the API, then connects to LiveKit.
   * Idempotent — leaves any current room before joining the new one.
   */
  async joinMeeting(meetingId: string): Promise<void> {
    if (this.room) {
      await this.leaveMeeting();
    }

    // 1. Get token from our backend
    const response = await firstValueFrom(this.meetingApi.join(meetingId));

    // 2. Create + wire the LiveKit Room
    const room = new Room({
      adaptiveStream: true,           // reduce quality on bad networks
      dynacast: true,                 // pause unpublished tracks server-side
    });

    this.wireEvents(room);

    // 3. Connect + publish local camera + mic
    // await room.connect(response.livekitWsUrl, response.livekitToken);
    // await room.localParticipant.enableCameraAndMicrophone();
        // 3. Connect + publish local camera + mic (graceful fallback if devices busy)
    await room.connect(response.livekitWsUrl, response.livekitToken);

    try {
      await room.localParticipant.enableCameraAndMicrophone();
    } catch (err) {
      console.warn('Camera+mic unavailable, trying mic-only', err);
      try {
        await room.localParticipant.setMicrophoneEnabled(true);
      } catch (micErr) {
        console.warn('Mic also unavailable, joining as listener only', micErr);
      }
    }

    this.room = room;
    this.currentMeetingId.set(response.meetingId);
    this.currentMeetingTitle.set(response.title);
    this.isHost.set(response.isHost);
    this.localParticipant.set(room.localParticipant);
    this.refreshRemoteParticipants();
  }

  /** Disconnect from the LiveKit room and clear signals. */
  async leaveMeeting(): Promise<void> {
    if (!this.room) {
      return;
    }
    const room = this.room;
    this.room = null;
    await room.disconnect();
    // Signals cleared in the `Disconnected` event handler
  }

  /** Toggle the local user's camera. */
  async toggleCamera(): Promise<void> {
    if (!this.room) return;
    const lp = this.room.localParticipant;
    await lp.setCameraEnabled(!lp.isCameraEnabled);
  }

  /** Toggle the local user's microphone. */
  async toggleMic(): Promise<void> {
    if (!this.room) return;
    const lp = this.room.localParticipant;
    await lp.setMicrophoneEnabled(!lp.isMicrophoneEnabled);
  }

  /** True if the local camera track is publishing. */
  isCameraOn(): boolean {
    return this.room?.localParticipant.isCameraEnabled ?? false;
  }

  /** True if the local mic track is publishing. */
  isMicOn(): boolean {
    return this.room?.localParticipant.isMicrophoneEnabled ?? false;
  }

  // ── Internal wiring ─────────────────────────────────────

  private wireEvents(room: Room): void {
    room
      .on(RoomEvent.ConnectionStateChanged, (state) => {
        this.connectionState.set(state);
        if (state === ConnectionState.Disconnected) {
          this.clearState();
        }
      })
      .on(RoomEvent.ParticipantConnected, () => this.refreshRemoteParticipants())
      .on(RoomEvent.ParticipantDisconnected, () => this.refreshRemoteParticipants())
      .on(RoomEvent.TrackSubscribed, () => this.refreshRemoteParticipants())
      .on(RoomEvent.TrackUnsubscribed, () => this.refreshRemoteParticipants())
      .on(RoomEvent.TrackMuted, () => this.refreshRemoteParticipants())
      .on(RoomEvent.TrackUnmuted, () => this.refreshRemoteParticipants())
      .on(RoomEvent.RecordingStatusChanged, (recording) => this.isRecording.set(recording))
      .on(RoomEvent.LocalTrackPublished, () => this.localParticipant.set(room.localParticipant))
      .on(RoomEvent.LocalTrackUnpublished, () => this.localParticipant.set(room.localParticipant));
  }

  private refreshRemoteParticipants(): void {
    if (!this.room) {
      this.remoteParticipants.set([]);
      return;
    }
    this.remoteParticipants.set(Array.from(this.room.remoteParticipants.values()));
  }

  private clearState(): void {
    this.remoteParticipants.set([]);
    this.localParticipant.set(null);
    this.isRecording.set(false);
    this.currentMeetingId.set(null);
    this.currentMeetingTitle.set('');
    this.isHost.set(false);
  }
}

/** Utility: pick a video track element from a participant (used by the UI). */
export function getVideoTrack(participant: LocalParticipant | RemoteParticipant): MediaStreamTrack | undefined {
  return participant
    .getTrackPublications()
    .find((pub) => pub.kind === Track.Kind.Video && !pub.isMuted)
    ?.track?.mediaStreamTrack;
}