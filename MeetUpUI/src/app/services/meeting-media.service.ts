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

    stream.getVideoTracks().forEach((track) => {
      track.enabled = this.isCameraOn();
    });
    stream.getAudioTracks().forEach((track) => {
      track.enabled = this.isMicOn();
    });

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

  toggleCamera(): void {
    const next = !this.isCameraOn();
    this.isCameraOn.set(next);

    const stream = this.streamState();
    if (!stream) {
      return;
    }

    stream.getVideoTracks().forEach((track) => {
      track.enabled = next;
    });
  }

  toggleMic(): void {
    const next = !this.isMicOn();
    this.isMicOn.set(next);

    const stream = this.streamState();
    if (!stream) {
      return;
    }

    stream.getAudioTracks().forEach((track) => {
      track.enabled = next;
    });
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
