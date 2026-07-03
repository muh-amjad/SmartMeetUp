import { CommonModule } from '@angular/common';
import { AfterViewInit, Component, ElementRef, ViewChild, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { gsap } from 'gsap';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-signup',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './signup.html',
  styleUrl: './signup.css',
})
export class SignupPage implements AfterViewInit {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);

  @ViewChild('card', { static: true })
  cardRef!: ElementRef<HTMLElement>;

  readonly errorMessage = signal('');
  readonly isSubmitting = signal(false);

  readonly signupForm = this.fb.group({
    username: ['', [Validators.required, Validators.minLength(3)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
  });

  private readonly formStatus = toSignal(this.signupForm.statusChanges, { initialValue: this.signupForm.status });

  readonly canSubmit = computed(() => this.formStatus() === 'VALID' && !this.isSubmitting());

  ngAfterViewInit(): void {
    gsap.from(this.cardRef.nativeElement, {
      y: 28,
      opacity: 0,
      duration: 0.9,
      ease: 'power3.out',
    });
  }

  submit() {
    if (this.signupForm.invalid || this.isSubmitting()) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set('');

    this.authService
      .signup(this.signupForm.getRawValue() as { username: string; email: string; password: string })
      .subscribe({
        next: () => {
          this.isSubmitting.set(false);
          this.router.navigate(['/login']);
        },
        error: (error) => {
          this.isSubmitting.set(false);
          const rawError = Array.isArray(error?.error)
            ? error.error.join(', ')
            : typeof error?.error === 'string'
              ? error.error
              : error?.error?.toString?.() ?? '';

          const normalizedError = rawError.toLowerCase();
          if (normalizedError.includes('email is already registered') || normalizedError.includes('email') && normalizedError.includes('exists')) {
            this.errorMessage.set('Email already registered. Please use another email or log in.');
            return;
          }

          this.errorMessage.set(rawError || 'Sign up failed. Please try a different username or email.');
        },
      });
  }
}
