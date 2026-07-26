import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranscriptUtteranceDto } from '../../dtos/meetings/transcript.dto';

@Component({
  selector: 'app-transcript-viewer',
  standalone: true,
  templateUrl: './transcript-viewer.component.html',
  styleUrl: './transcript-viewer.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TranscriptViewerComponent {
  readonly utterances = input.required<TranscriptUtteranceDto[]>();

  /** Emits a start time in seconds when a line's timestamp is clicked (for audio seek). */
  readonly seek = output<number>();

  speakerName(u: TranscriptUtteranceDto): string {
    return u.participantUsername ?? `Speaker ${u.speakerLabel}`;
  }

  formatTimestamp(startMs: number): string {
    const totalSeconds = Math.floor(startMs / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    const pad = (n: number) => n.toString().padStart(2, '0');
    return hours > 0
      ? `${hours}:${pad(minutes)}:${pad(seconds)}`
      : `${pad(minutes)}:${pad(seconds)}`;
  }

  onSeek(startMs: number): void {
    this.seek.emit(startMs / 1000);
  }
}
