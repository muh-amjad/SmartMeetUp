import { ChangeDetectionStrategy, Component, computed, effect, ElementRef, inject, viewChild } from '@angular/core';
import { IncomingCallService } from '../../services/incoming-call.service';
import { LivekitMeetingService } from '../../services/livekit-meeting.service';
import { RingtoneService } from '../../services/ringtone.service';

/**
 * The incoming-call popup. Rendered once at the root, so a call rings on whatever page the user
 * is on — including inside another call, where answering switches over to the new one.
 */
@Component({
  selector: 'app-incoming-call-dialog',
  standalone: true,
  templateUrl: './incoming-call-dialog.component.html',
  styleUrl: './incoming-call-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IncomingCallDialogComponent {
  private readonly calls = inject(IncomingCallService);
  private readonly livekit = inject(LivekitMeetingService);
  private readonly ringtone = inject(RingtoneService);

  readonly invite = this.calls.incoming;
  readonly answering = this.calls.answering;
  readonly ringing = this.ringtone.ringing;
  readonly audible = this.ringtone.audible;

  /** Answering means leaving the call the user is in now, so the popup says so. */
  readonly inAnotherCall = computed(() => !!this.livekit.currentMeetingId());

  readonly initial = computed(() => (this.invite()?.fromUsername.trim()[0] ?? '?').toUpperCase());

  private readonly acceptButton = viewChild<ElementRef<HTMLButtonElement>>('acceptButton');

  constructor() {
    // Focus moves to Answer when the popup opens, so a keyboard user can pick up straight away.
    effect(() => {
      this.acceptButton()?.nativeElement.focus();
    });
  }

  accept(): void {
    void this.calls.accept();
  }

  decline(): void {
    void this.calls.decline();
  }
}
