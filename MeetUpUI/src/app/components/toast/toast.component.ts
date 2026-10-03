import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-toast',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="toast-container">
      @for (toast of toastService.toasts(); track toast.id) {
        <div [class]="'toast ' + toast.type">
          {{ toast.message }}
        </div>
      }
    </div>
  `,
  styles: [`
    .toast-container {
      position: fixed;
      top: max(16px, env(safe-area-inset-top));
      right: 16px;
      /* Never wider than the screen: long messages wrap instead of running off a phone. */
      max-width: min(380px, calc(100vw - 32px));
      z-index: 9999;
      pointer-events: none;
    }

    .toast {
      margin-bottom: 10px;
      padding: 12px 16px;
      border-radius: 8px;
      font-size: 14px;
      line-height: 1.4;
      overflow-wrap: anywhere;
      box-shadow: 0 10px 28px rgba(0, 0, 0, 0.45);
      pointer-events: auto;
      animation: slideIn 0.3s ease-in-out;
    }

    .toast.success {
      background-color: #4caf50;
      color: white;
    }

    .toast.error {
      background-color: #f44336;
      color: white;
    }

    .toast.info {
      background-color: #2196f3;
      color: white;
    }

    @keyframes slideIn {
      from {
        transform: translateX(400px);
        opacity: 0;
      }
      to {
        transform: translateX(0);
        opacity: 1;
      }
    }
  `]
})
export class ToastComponent {
  toastService = inject(ToastService);
}
