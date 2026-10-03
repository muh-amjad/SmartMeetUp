import { Injectable, signal } from '@angular/core';

/** One ring cycle: two short bursts, then a pause — the familiar "ring-ring … ring-ring". */
const CycleMs = 3_000;
const BurstSeconds = 0.4;
const BurstGapSeconds = 0.2;
const ToneHz = [400, 450];
const Volume = 0.16;

/** Buzz pattern for phones, matching the ring cycle. */
const VibratePattern = [400, 200, 400, 2_000];

/**
 * Plays the incoming-call ringtone, synthesised with the Web Audio API so there is no audio file
 * to ship or load.
 *
 * Browsers only let a page make sound after the user has interacted with it, and a call arriving
 * over the network does not count. So the audio context is created and unlocked on the first
 * click or key press anywhere in the app — typically signing in — and is ready by the time a call
 * comes in. If it is still locked (a page reloaded and never touched), the popup shows silently
 * and phones still vibrate.
 */
@Injectable({ providedIn: 'root' })
export class RingtoneService {
  private context: AudioContext | null = null;
  private output: GainNode | null = null;
  private cycleTimer: ReturnType<typeof setInterval> | null = null;

  /** True while the ringtone is meant to be playing. */
  readonly ringing = signal(false);

  /** Whether the browser is actually letting it make sound. */
  readonly audible = signal(false);

  private readonly unlock = () => {
    const context = this.ensureContext();
    if (!context) {
      return;
    }
    context
      .resume()
      .then(() => {
        if (context.state === 'running') {
          this.removeUnlockListeners();
        }
      })
      .catch(() => undefined);
  };

  constructor() {
    if (typeof document === 'undefined') {
      return;
    }
    document.addEventListener('pointerdown', this.unlock, true);
    document.addEventListener('keydown', this.unlock, true);
  }

  start(): void {
    if (this.ringing()) {
      return;
    }
    this.ringing.set(true);

    const context = this.ensureContext();
    if (context) {
      // A fresh output per ring, so stop() can silence bursts that are already scheduled.
      this.output = context.createGain();
      this.output.gain.value = Volume;
      this.output.connect(context.destination);

      void context
        .resume()
        .then(() => this.audible.set(context.state === 'running'))
        .catch(() => this.audible.set(false));
      this.audible.set(context.state === 'running');
    }

    this.playCycle();
    this.cycleTimer = setInterval(() => this.playCycle(), CycleMs);
  }

  stop(): void {
    if (!this.ringing()) {
      return;
    }
    this.ringing.set(false);

    if (this.cycleTimer !== null) {
      clearInterval(this.cycleTimer);
      this.cycleTimer = null;
    }
    // Disconnecting the output cuts off any burst still sounding or scheduled.
    this.output?.disconnect();
    this.output = null;

    if (typeof navigator !== 'undefined' && 'vibrate' in navigator) {
      navigator.vibrate(0);
    }
  }

  private playCycle(): void {
    this.vibrate();

    const context = this.context;
    const output = this.output;
    if (!context || !output) {
      return;
    }

    const start = context.currentTime + 0.05;
    this.burst(context, output, start);
    this.burst(context, output, start + BurstSeconds + BurstGapSeconds);
  }

  private burst(context: AudioContext, output: GainNode, start: number): void {
    const end = start + BurstSeconds;
    const envelope = context.createGain();
    // Short fades in and out, so each burst doesn't click.
    envelope.gain.setValueAtTime(0, start);
    envelope.gain.linearRampToValueAtTime(0.5, start + 0.02);
    envelope.gain.setValueAtTime(0.5, end - 0.03);
    envelope.gain.linearRampToValueAtTime(0, end);
    envelope.connect(output);

    for (const hz of ToneHz) {
      const tone = context.createOscillator();
      tone.type = 'sine';
      tone.frequency.value = hz;
      tone.connect(envelope);
      tone.start(start);
      tone.stop(end + 0.02);
    }
  }

  private vibrate(): void {
    if (typeof navigator !== 'undefined' && 'vibrate' in navigator) {
      try {
        navigator.vibrate(VibratePattern);
      } catch {
        // Some browsers refuse without a recent gesture; the popup still shows.
      }
    }
  }

  private ensureContext(): AudioContext | null {
    if (this.context) {
      return this.context;
    }
    const Ctor =
      typeof window === 'undefined'
        ? undefined
        : window.AudioContext ??
          (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) {
      return null;
    }
    this.context = new Ctor();
    return this.context;
  }

  private removeUnlockListeners(): void {
    document.removeEventListener('pointerdown', this.unlock, true);
    document.removeEventListener('keydown', this.unlock, true);
  }
}
