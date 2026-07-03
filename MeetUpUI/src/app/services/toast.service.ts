import { Injectable, Signal, signal } from '@angular/core';

export interface Toast {
  id: string;
  message: string;
  type: 'success' | 'error' | 'info';
}

@Injectable({
  providedIn: 'root'
})
export class ToastService {
  private toastSignal = signal<Toast[]>([]);
  public toasts: Signal<Toast[]> = this.toastSignal.asReadonly();

  private nextId = 0;

  success(message: string): void {
    this.addToast(message, 'success');
  }

  error(message: string): void {
    this.addToast(message, 'error');
  }

  info(message: string): void {
    this.addToast(message, 'info');
  }

  private addToast(message: string, type: Toast['type']): void {
    const id = `toast-${this.nextId++}`;
    const toast: Toast = { id, message, type };

    this.toastSignal.update(toasts => [...toasts, toast]);

    setTimeout(() => {
      this.removeToast(id);
    }, 4000);
  }

  private removeToast(id: string): void {
    this.toastSignal.update(toasts => toasts.filter(t => t.id !== id));
  }
}
