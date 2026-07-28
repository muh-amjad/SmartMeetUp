import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../services/auth.service';

interface NavItem {
  path: string;
  label: string;
  /** Single glyph rendered as a decorative mark; the label carries the meaning. */
  mark: string;
}

/**
 * Persistent chrome for the signed-in app: left nav, top bar with search, and the routed page.
 * The call room deliberately sits outside this shell so a meeting can use the full window.
 */
@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [FormsModule, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShellComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly nav: ReadonlyArray<NavItem> = [
    { path: '/dashboard', label: 'Dashboard', mark: '◧' },
    { path: '/meetings', label: 'Meetings', mark: '▤' },
    { path: '/action-items', label: 'Action items', mark: '✓' },
    { path: '/analytics', label: 'Analytics', mark: '▨' },
    { path: '/settings', label: 'Settings', mark: '⚙' },
  ];

  readonly searchQuery = signal('');
  readonly menuOpen = signal(false);

  readonly username = computed(() => this.auth.currentUser()?.username ?? '');
  readonly email = computed(() => this.auth.currentUser()?.email ?? '');
  readonly initials = computed(() => {
    const name = this.username().trim();
    if (!name) {
      return '?';
    }
    const parts = name.split(/[\s._-]+/).filter(Boolean);
    return parts.length > 1
      ? (parts[0][0] + parts[1][0]).toUpperCase()
      : name.slice(0, 2).toUpperCase();
  });

  submitSearch(): void {
    const q = this.searchQuery().trim();
    if (!q) {
      return;
    }
    this.router.navigate(['/search'], { queryParams: { q, mode: 'hybrid' } });
  }

  toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  closeMenu(): void {
    this.menuOpen.set(false);
  }

  async logout(): Promise<void> {
    this.closeMenu();
    await this.auth.logout();
    this.router.navigate(['/login']);
  }
}
