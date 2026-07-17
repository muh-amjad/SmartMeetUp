import { CommonModule } from '@angular/common';
import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { MeetingMediaService } from '../../services/meeting-media.service';

@Component({
  selector: 'app-meeting-preview',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './meeting-preview.html',
  styleUrl: './meeting-preview.css',
})
export class MeetingPreviewPage implements AfterViewInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly meetingMediaService = inject(MeetingMediaService);

  @ViewChild('previewVideo', { static: true })
  previewVideoRef!: ElementRef<HTMLVideoElement>;

  readonly errorMessage = signal('');

  get isCameraOn(): boolean {
    return this.meetingMediaService.isCameraOn();
  }

  get isMicOn(): boolean {
    return this.meetingMediaService.isMicOn();
  }

  async ngAfterViewInit(): Promise<void> {
    await this.startPreview();
  }

  ngOnDestroy(): void {
    // The meeting room acquires its own camera/mic via LiveKit, so the
    // preview's stream is never reused — always release it here, otherwise
    // it keeps the camera/mic hardware busy in the background indefinitely.
    this.meetingMediaService.stopStream();
  }

  async startPreview(): Promise<void> {
    this.errorMessage.set('');

    try {
      await this.meetingMediaService.attachStream(this.previewVideoRef.nativeElement);
    } catch {
      this.errorMessage.set('Camera and microphone access is required to start a meeting.');
    }
  }

  toggleCamera(): void {
    void this.meetingMediaService.toggleCamera();
  }

  toggleMic(): void {
    void this.meetingMediaService.toggleMic();
  }

  joinNow(): void {
    // Release the preview's camera/mic now — the meeting room acquires its
    // own via LiveKit, so keeping this stream alive would just leave the
    // hardware running in the background with nothing using it.
    this.meetingMediaService.stopStream();
    this.router.navigate(['/meet'], { state: { source: 'join-now' } });
  }

  cancel(): void {
    this.meetingMediaService.stopStream();
    this.router.navigate(['/dashboard']);
  }
}
