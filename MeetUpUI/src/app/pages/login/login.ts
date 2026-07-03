import { CommonModule } from '@angular/common';
import { AfterViewInit, Component, ElementRef, ViewChild, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { gsap } from 'gsap';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, RouterLink],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class LoginPage implements AfterViewInit {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);

  @ViewChild('card', { static: true })
  cardRef!: ElementRef<HTMLElement>;

  readonly errorMessage = signal('');
  readonly isSubmitting = signal(false);

  readonly loginForm = this.fb.group({
    usernameOrEmail: ['', [Validators.required]],
    password: ['', [Validators.required]],
  });

  get canSubmit(): boolean {
    return this.loginForm.valid && !this.isSubmitting();
  }

  ngAfterViewInit(): void {
    gsap.from(this.cardRef.nativeElement, {
      y: 28,
      opacity: 0,
      duration: 0.9,
      ease: 'power3.out',
    });
  }

  submit() {
    if (this.loginForm.invalid || this.isSubmitting()) {
      return;
    }

    this.isSubmitting.set(true);
    this.errorMessage.set('');

    this.authService.login(this.loginForm.getRawValue() as { usernameOrEmail: string; password: string }).subscribe({
      next: () => {
        this.isSubmitting.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (error) => {
        this.isSubmitting.set(false);
        this.errorMessage.set(error?.error?.toString?.() || 'Login failed. Check your credentials and try again.');
      },
    });
  }
}
