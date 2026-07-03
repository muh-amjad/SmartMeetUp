import { CommonModule } from '@angular/common';
import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { gsap } from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-home',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './home.html',
  styleUrl: './home.css',
})
export class HomePage implements AfterViewInit, OnDestroy {
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);
  private sliderTimer?: ReturnType<typeof setInterval>;

  readonly isMenuOpen = signal(false);
  readonly activeSlide = signal(0);
  readonly linkedInUrl = 'https://www.linkedin.com/in/your-profile';

  readonly slides = [
    {
      imageUrl:
        'https://images.unsplash.com/photo-1521737711867-e3b97375f902?auto=format&fit=crop&w=1400&q=80',
      title: 'Remote brainstorming sessions',
      subtitle: 'Run fast standups and planning calls across time zones.',
    },
    {
      imageUrl:
        'https://images.unsplash.com/photo-1552664730-d307ca884978?auto=format&fit=crop&w=1400&q=80',
      title: 'Face-to-face collaboration',
      subtitle: 'Keep teams connected with high quality browser meetings.',
    },
    {
      imageUrl:
        'https://images.unsplash.com/photo-1517048676732-d65bc937f952?auto=format&fit=crop&w=1400&q=80',
      title: 'Global catch-ups that feel local',
      subtitle: 'Invite teammates and start virtual conversations instantly.',
    },
  ];

  @ViewChild('hero', { static: true })
  heroRef!: ElementRef<HTMLElement>;

  @ViewChild('sliderSection', { static: true })
  sliderSectionRef!: ElementRef<HTMLElement>;

  ngAfterViewInit(): void {
    gsap.registerPlugin(ScrollTrigger);

    const hero = this.heroRef.nativeElement;

    gsap.from(hero.querySelectorAll('.nav-shell, .hero-kicker, .hero-title, .hero-subtitle, .cta-row'), {
      y: 24,
      opacity: 0,
      duration: 0.9,
      stagger: 0.12,
      ease: 'power3.out',
    });

    gsap.to('.hero-orb', {
      y: 16,
      rotation: 8,
      repeat: -1,
      yoyo: true,
      duration: 4,
      ease: 'sine.inOut',
    });

    gsap.to('.parallax-shape', {
      yPercent: -20,
      scrollTrigger: {
        trigger: this.sliderSectionRef.nativeElement,
        start: 'top bottom',
        end: 'bottom top',
        scrub: true,
      },
    });

    this.sliderTimer = setInterval(() => {
      const nextIndex = (this.activeSlide() + 1) % this.slides.length;
      this.activeSlide.set(nextIndex);
    }, 3800);
  }

  ngOnDestroy(): void {
    if (this.sliderTimer) {
      clearInterval(this.sliderTimer);
      this.sliderTimer = undefined;
    }

    ScrollTrigger.getAll().forEach((trigger) => trigger.kill());
  }

  toggleMenu(): void {
    this.isMenuOpen.update((current) => !current);
  }

  closeMenu(): void {
    this.isMenuOpen.set(false);
  }

  goMeetNow(): void {
    this.closeMenu();

    if (this.authService.isAuthenticated()) {
      this.router.navigate(['/preview']);
      return;
    }

    this.router.navigate(['/login']);
  }

  openSupport(): void {
    window.alert('Support chat button is ready. We can wire real chat behavior next.');
  }

  slideClass(index: number): string {
    if (index === this.activeSlide()) {
      return 'slide-card active';
    }

    if (index < this.activeSlide()) {
      return 'slide-card previous';
    }

    return 'slide-card';
  }

  trackByTitle(_: number, slide: { title: string }): string {
    return slide.title;
  }

  openExternalProfile(event: Event): void {
    event.preventDefault();
    window.open(this.linkedInUrl, '_blank', 'noopener,noreferrer');
  }

  selectSlide(index: number): void {
    this.activeSlide.set(index);

    if (this.sliderTimer) {
      clearInterval(this.sliderTimer);
      this.sliderTimer = setInterval(() => {
        const nextIndex = (this.activeSlide() + 1) % this.slides.length;
        this.activeSlide.set(nextIndex);
      }, 3800);
    }
  }

  navigate(route: '/signup' | '/login'): void {
    this.closeMenu();
    this.router.navigate([route]);
  }

  get year(): number {
    return new Date().getFullYear();
  }

  get isLoggedIn(): boolean {
    return this.authService.isAuthenticated();
  }

  get meetNowLabel(): string {
    return this.isLoggedIn ? 'Create Meeting' : 'Meet Now';
  }

  get profileHint(): string {
    return 'Replace linkedInUrl in home.ts with your profile URL.';
  }

  get menuLabel(): string {
    return this.isMenuOpen() ? 'Close menu' : 'Open menu';
  }

  get authButtonLabel(): string {
    return this.isLoggedIn ? 'Dashboard' : 'Login';
  }

  goPrimaryAuth(): void {
    this.closeMenu();
    this.router.navigate([this.isLoggedIn ? '/dashboard' : '/login']);
  }

  goSignup(): void {
    this.navigate('/signup');
  }

  goLogin(): void {
    this.navigate('/login');
  }

  goDashboard(): void {
    this.closeMenu();
    this.router.navigate(['/dashboard']);
  }

  goIntroAction(): void {
    if (this.isLoggedIn) {
      this.goDashboard();
      return;
    }

    this.goSignup();
  }

  openLinkedInPlaceholder(event: Event): void {
    this.openExternalProfile(event);
  }

  goMeetNowFromHero(): void {
    this.goMeetNow();
  }
}
