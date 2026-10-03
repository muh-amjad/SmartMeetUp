import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { IncomingCallService } from './services/incoming-call.service';
import { LivekitMeetingService } from './services/livekit-meeting.service';
import { RingtoneService } from './services/ringtone.service';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter([]),
        {
          provide: IncomingCallService,
          useValue: { incoming: signal(null), answering: signal(false) },
        },
        { provide: RingtoneService, useValue: { ringing: signal(false), audible: signal(false) } },
        { provide: LivekitMeetingService, useValue: { currentMeetingId: signal(null) } },
      ],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('should render router outlet shell', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('router-outlet')).toBeTruthy();
  });

  it('shows no call popup when nothing is ringing', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('[role="alertdialog"]')).toBeNull();
  });
});
