import { inject, Injectable, signal } from '@angular/core';
import {
  ConnectionState,
  LocalParticipant,
  Participant,
  RemoteParticipant,
  Room,
  RoomEvent,
  Track,
} from 'livekit-client';
import { firstValueFrom } from 'rxjs';
import { MeetingApiService } from './meeting-api.service';
import { SignalrService, SpeakingInterval } from './signalr.service';

/**
 * Wraps a LiveKit Room object and exposes reactive signals for the UI.
 * Replaces the mesh WebRTC logic that WebrtcPeerService used to handle in Phase 0.
 */
@Injectable({ providedIn: 'root' })
export class LivekitMeetingService {
  private readonly meetingApi = inject(MeetingApiService);
  private readonly signalR = inject(SignalrService);

  private room: Room | null = null;

  /**
   * How often finished speaking turns are pushed to the server during a call. Short, because
   * anything still buffered when a tab is closed outright is at risk, and every turn that never
   * arrives is a stretch of transcript that cannot be attributed to its speaker.
   */
  private static readonly SpeakingFlushIntervalMs = 5_000;

  /** Upper bound on turns held for retry while the connection is down. */
  private static readonly MaxPendingSpeakingIntervals = 1_000;

  private speakingSince: Date | null = null;
  private pendingSpeakingIntervals: SpeakingInterval[] = [];
  private speakingFlushTimer: ReturnType<typeof setInterval> | null = null;

  /** Closing the tab skips Angular teardown entirely; this closes the open turn and sends it. */
  private readonly onPageHide = () => {
    if (this.room) {
      this.trackLocalSpeaking(this.room, []);
    }
    void this.flushSpeakingIntervals();
  };

  // ── Public reactive state ────────────────────────────────
  readonly connectionState = signal<ConnectionState>(ConnectionState.Disconnected);
  readonly localParticipant = signal<LocalParticipant | null>(null);
  readonly remoteParticipants = signal<RemoteParticipant[]>([]);
  readonly isRecording = signal<boolean>(false);
  readonly currentMeetingId = signal<string | null>(null);
  readonly currentMeetingTitle = signal<string>('');
  readonly isHost = signal<boolean>(false);

  /**
   * Local camera and mic state as signals. The room object mutates in place, and the local
   * participant is always the same object, so re-setting `localParticipant` after a toggle is a
   * no-op to Angular — the buttons and the self-view would not update until something unrelated
   * re-rendered. These change exactly when the hardware state does.
   */
  readonly cameraEnabled = signal<boolean>(false);
  readonly micEnabled = signal<boolean>(false);

  /** Bumped whenever a local track is (un)published, so the self-view re-attaches its video. */
  readonly localTrackVersion = signal<number>(0);

  /** Identities currently speaking, for highlighting their tiles. */
  readonly activeSpeakerIds = signal<ReadonlySet<string>>(new Set());

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
      // When we unpublish a track (e.g. camera off), stop the underlying
      // MediaStreamTrack too — this releases the hardware so the OS camera
      // light goes off. Default is true in recent versions; explicit for safety.
      stopLocalTrackOnUnpublish: true,
    });

    this.wireEvents(room);

    // 3. Connect. LiveKit's default peerConnectionTimeout (15s) is too tight
    // for this environment — ICE/DTLS negotiation through Docker Desktop's
    // NAT (see docker-compose.yml) routinely takes longer than that even
    // though it does complete, so connect() rejects with "could not
    // establish pc connection" moments before the peer connection actually
    // comes up. Give it real breathing room instead of just papering over a
    // too-short timeout.
    try {
      await room.connect(response.livekitWsUrl, response.livekitToken, {
        peerConnectionTimeout: 30_000,
        websocketTimeout: 30_000,
      });
    } catch (err) {
      console.warn('LiveKit connect() rejected; waiting to see if the room recovers', err);
      const recovered = await this.waitForConnected(room, 8000);
      if (!recovered) {
        room.disconnect();
        throw err;
      }
      console.info('LiveKit connection recovered after a transient error');
    }

    // 4. Publish local camera + mic (graceful fallback if devices busy)
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

    // 5. Unblock audio playback. Browsers refuse to play audio that was not started by a user
    // gesture, and a remote track arriving over the network does not count as one. joinMeeting is
    // always reached from a click, so this call is still inside that gesture's window and is
    // allowed. Without it the audio elements stay silently paused and the call has no sound —
    // with nothing logged to explain why.
    if (!room.canPlaybackAudio) {
      try {
        await room.startAudio();
      } catch (err) {
        console.warn('Audio playback is still blocked; it will need a user interaction', err);
      }
    }

    this.room = room;
    this.currentMeetingId.set(response.meetingId);
    this.currentMeetingTitle.set(response.title);
    this.isHost.set(response.isHost);
    this.syncLocalState(room);
    // Seed from the join response rather than waiting for RecordingStatusChanged: the SDK drops
    // that event while the room is still connecting, so anyone joining a call that is already
    // being recorded (every invitee, or a refresh) would never see the recording indicator.
    this.isRecording.set(room.isRecording);
    this.refreshRemoteParticipants();
    this.startSpeakingFlushTimer();
  }

  /** Disconnect from the LiveKit room and clear signals. */
  async leaveMeeting(): Promise<void> {
    if (!this.room) {
      return;
    }
    const room = this.room;
    this.room = null;

    // Close any turn still open, then push everything before the meeting id is cleared.
    this.stopSpeakingFlushTimer();
    this.trackLocalSpeaking(room, []);
    await this.flushSpeakingIntervals();

    // Explicitly disable camera + mic first. This unpublishes the tracks
    // and (with stopLocalTrackOnUnpublish=true) stops them, releasing the
    // OS-level camera/mic hardware so the indicator light turns off.
    try {
      await room.localParticipant.setCameraEnabled(false);
      await room.localParticipant.setMicrophoneEnabled(false);
    } catch (err) {
      console.warn('Error disabling local tracks on leave', err);
    }

    // Belt + suspenders: also stop any remaining local tracks (e.g. screen share).
    for (const pub of room.localParticipant.getTrackPublications()) {
      try {
        pub.track?.stop();
      } catch {
        /* ignore */
      }
    }

    await room.disconnect();
    // Signals cleared in the `Disconnected` event handler
  }

  /** Toggle the local user's camera. When disabling, stop the track fully so
   *  the camera hardware light turns off (LiveKit only mutes by default).
   *  The stop runs in `finally` so hardware is released even if the SDK call
   *  itself throws — we never want to leave the camera running silently. */
  async toggleCamera(): Promise<void> {
    if (!this.room) return;
    const lp = this.room.localParticipant;
    const willEnable = !lp.isCameraEnabled;

    try {
      await lp.setCameraEnabled(willEnable);
    } finally {
      if (!willEnable) {
        lp.getTrackPublications()
          .filter((pub) => pub.kind === 'video')
          .forEach((pub) => pub.track?.stop());
      }
      this.syncLocalState(this.room);
    }
  }

  /** Toggle the local user's microphone. When disabling, stop the track fully
   *  so the mic hardware indicator turns off. Same `finally` guarantee as
   *  toggleCamera. */
  async toggleMic(): Promise<void> {
    if (!this.room) return;
    const lp = this.room.localParticipant;
    const willEnable = !lp.isMicrophoneEnabled;

    try {
      await lp.setMicrophoneEnabled(willEnable);
    } finally {
      if (!willEnable) {
        lp.getTrackPublications()
          .filter((pub) => pub.kind === 'audio')
          .forEach((pub) => pub.track?.stop());
      }
      this.syncLocalState(this.room);
    }
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

  /** Resolves true as soon as `room` reaches Connected, false if it instead
   *  settles on Disconnected or the timeout elapses first. Relies on the
   *  ConnectionStateChanged listener already attached by wireEvents(). */
  private waitForConnected(room: Room, timeoutMs: number): Promise<boolean> {
    if (room.state === ConnectionState.Connected) {
      return Promise.resolve(true);
    }

    return new Promise((resolve) => {
      const onStateChanged = (state: ConnectionState) => {
        if (state === ConnectionState.Connected) {
          cleanup();
          resolve(true);
        } else if (state === ConnectionState.Disconnected) {
          cleanup();
          resolve(false);
        }
      };
      const timer = setTimeout(() => {
        cleanup();
        resolve(false);
      }, timeoutMs);
      const cleanup = () => {
        clearTimeout(timer);
        room.off(RoomEvent.ConnectionStateChanged, onStateChanged);
      };

      room.on(RoomEvent.ConnectionStateChanged, onStateChanged);
    });
  }

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
      .on(RoomEvent.LocalTrackPublished, () => this.syncLocalState(room))
      .on(RoomEvent.LocalTrackUnpublished, () => this.syncLocalState(room))
      .on(RoomEvent.ActiveSpeakersChanged, (speakers) => {
        this.activeSpeakerIds.set(new Set(speakers.map((s) => s.identity)));
        this.trackLocalSpeaking(room, speakers);
      });
  }

  /** Pushes the local participant's current hardware state into the signals the UI reads. */
  private syncLocalState(room: Room | null): void {
    if (!room) {
      return;
    }
    this.localParticipant.set(room.localParticipant);
    this.cameraEnabled.set(room.localParticipant.isCameraEnabled);
    this.micEnabled.set(room.localParticipant.isMicrophoneEnabled);
    this.localTrackVersion.update((v) => v + 1);
  }

  /**
   * Turns the active-speaker signal into completed speaking turns for this client only. Each client
   * reports its own turns; the server attributes them to the authenticated caller, so nobody can
   * claim speaking time on someone else's behalf.
   */
  private trackLocalSpeaking(room: Room, speakers: Participant[]): void {
    const localIdentity = room.localParticipant.identity;
    const speakingNow = speakers.some((s) => s.identity === localIdentity);

    if (speakingNow && this.speakingSince === null) {
      this.speakingSince = new Date();
      return;
    }

    if (!speakingNow && this.speakingSince !== null) {
      const startedUtc = this.speakingSince;
      this.speakingSince = null;

      const stoppedUtc = new Date();
      // Sub-100ms blips are noise rather than speech, and would only add rows to overlap against.
      if (stoppedUtc.getTime() - startedUtc.getTime() >= 100) {
        this.pendingSpeakingIntervals.push({
          startedUtc: startedUtc.toISOString(),
          stoppedUtc: stoppedUtc.toISOString(),
        });
      }
    }
  }

  private startSpeakingFlushTimer(): void {
    this.stopSpeakingFlushTimer();
    this.speakingFlushTimer = setInterval(
      () => void this.flushSpeakingIntervals(),
      LivekitMeetingService.SpeakingFlushIntervalMs,
    );
    window.addEventListener('pagehide', this.onPageHide);
  }

  private stopSpeakingFlushTimer(): void {
    if (this.speakingFlushTimer !== null) {
      clearInterval(this.speakingFlushTimer);
      this.speakingFlushTimer = null;
    }
    window.removeEventListener('pagehide', this.onPageHide);
  }

  /**
   * Sends whatever speaking turns have accumulated. A failed send never interrupts the call, but
   * the batch is put back and goes out with the next flush. These turns are the only record of who
   * was speaking when; the server cannot reconstruct a lost one, and its transcript lines then
   * stay "Speaker B".
   */
  private async flushSpeakingIntervals(): Promise<void> {
    const meetingId = this.currentMeetingId();
    if (!meetingId || this.pendingSpeakingIntervals.length === 0) {
      return;
    }

    const batch = this.pendingSpeakingIntervals;
    this.pendingSpeakingIntervals = [];

    try {
      await this.signalR.reportSpeakingIntervals(meetingId, batch);
    } catch (err) {
      console.warn('Could not report speaking intervals; will retry', err);
      this.pendingSpeakingIntervals = [...batch, ...this.pendingSpeakingIntervals].slice(
        -LivekitMeetingService.MaxPendingSpeakingIntervals,
      );
    }
  }

  private refreshRemoteParticipants(): void {
    if (!this.room) {
      this.remoteParticipants.set([]);
      return;
    }
    this.remoteParticipants.set(Array.from(this.room.remoteParticipants.values()));
  }

  private clearState(): void {
    this.stopSpeakingFlushTimer();
    this.speakingSince = null;
    this.pendingSpeakingIntervals = [];
    this.remoteParticipants.set([]);
    this.localParticipant.set(null);
    this.cameraEnabled.set(false);
    this.micEnabled.set(false);
    this.activeSpeakerIds.set(new Set());
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