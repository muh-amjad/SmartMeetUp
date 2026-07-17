import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class MeetingMediaService {
  private readonly streamState = signal<MediaStream | null>(null);
  readonly localStream = this.streamState.asReadonly();

  readonly isCameraOn = signal(false);
  readonly isMicOn = signal(false);

  async ensureLocalStream(): Promise<MediaStream> {
    const existing = this.streamState();
    if (existing) {
      return existing;
    }

    const stream = await navigator.mediaDevices.getUserMedia({
      video: true,
      audio: true,
    });

    // Acquiring the stream actually starts the hardware, so reflect that in
    // the toggle state instead of leaving it out of sync with reality.
    this.isCameraOn.set(true);
    this.isMicOn.set(true);

    this.streamState.set(stream);
    return stream;
  }

  async attachStream(videoElement: HTMLVideoElement): Promise<void> {
    const stream = await this.ensureLocalStream();
    videoElement.srcObject = stream;

    try {
      await videoElement.play();
    } catch {
      // Some browsers block autoplay until user interaction.
    }
  }

  /** Turning the camera off stops the underlying hardware track so the OS
   *  camera indicator actually turns off — merely disabling a track keeps
   *  the device capturing. Turning back on re-acquires a fresh track, since
   *  a stopped MediaStreamTrack can never be restarted. */
  async toggleCamera(): Promise<void> {
    const stream = this.streamState();
    if (!stream) {
      this.isCameraOn.set(!this.isCameraOn());
      return;
    }

    if (this.isCameraOn()) {
      stream.getVideoTracks().forEach((track) => {
        track.stop();
        stream.removeTrack(track);
      });
      this.isCameraOn.set(false);
    } else {
      const videoStream = await navigator.mediaDevices.getUserMedia({ video: true });
      stream.addTrack(videoStream.getVideoTracks()[0]);
      this.isCameraOn.set(true);
    }
  }

  /** Same hardware-release guarantee as toggleCamera, for the microphone. */
  async toggleMic(): Promise<void> {
    const stream = this.streamState();
    if (!stream) {
      this.isMicOn.set(!this.isMicOn());
      return;
    }

    if (this.isMicOn()) {
      stream.getAudioTracks().forEach((track) => {
        track.stop();
        stream.removeTrack(track);
      });
      this.isMicOn.set(false);
    } else {
      const audioStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      stream.addTrack(audioStream.getAudioTracks()[0]);
      this.isMicOn.set(true);
    }
  }

  stopStream(): void {
    const stream = this.streamState();
    if (!stream) {
      return;
    }

    stream.getTracks().forEach((track) => track.stop());
    this.streamState.set(null);
  }
}
