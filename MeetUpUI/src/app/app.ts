import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { IncomingCallDialogComponent } from './components/incoming-call/incoming-call-dialog.component';
import { ToastComponent } from './components/toast/toast.component';
import { IncomingCallService } from './services/incoming-call.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastComponent, IncomingCallDialogComponent],
  templateUrl: './app.html',
  styleUrl: './app.css',
  standalone: true,
})
export class App {
  constructor() {
    // Keeps the signed-in user reachable for calls on every page, not just the call screens.
    inject(IncomingCallService);
  }
}
