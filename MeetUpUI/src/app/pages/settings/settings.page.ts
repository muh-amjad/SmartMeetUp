import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AnalysisProviderDto } from '../../dtos/meetings/analysis.dto';
import { AiProviderService } from '../../services/ai-provider.service';
import { ToastService } from '../../services/toast.service';

/** Sentinel for "no explicit preference" — the server treats null as "use the system default". */
const USE_DEFAULT = '';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [DecimalPipe, FormsModule],
  templateUrl: './settings.page.html',
  styleUrl: './settings.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SettingsPage implements OnInit {
  private readonly aiProviders = inject(AiProviderService);
  private readonly toast = inject(ToastService);

  readonly useDefault = USE_DEFAULT;

  readonly providers = signal<AnalysisProviderDto[]>([]);
  readonly selectedProviderKey = signal<string>(USE_DEFAULT);
  readonly loading = signal(true);
  readonly saving = signal(false);

  // ── Account ───────────────────────────────────────
  readonly username = signal('');
  readonly email = signal('');
  readonly displayName = signal('');
  readonly savingProfile = signal(false);

  readonly currentPassword = signal('');
  readonly newPassword = signal('');
  readonly confirmPassword = signal('');
  readonly savingPassword = signal(false);
  readonly passwordError = signal('');

  async ngOnInit(): Promise<void> {
    this.loading.set(true);
    try {
      const [providers, preferences, profile] = await Promise.all([
        firstValueFrom(this.aiProviders.getProviders()),
        firstValueFrom(this.aiProviders.getPreferences()),
        firstValueFrom(this.aiProviders.getProfile()),
      ]);
      this.providers.set(providers);
      this.selectedProviderKey.set(preferences.preferredAnalysisProviderKey ?? USE_DEFAULT);
      this.username.set(profile.username);
      this.email.set(profile.email);
      this.displayName.set(profile.displayName);
    } catch {
      this.toast.error('Could not load settings.');
    } finally {
      this.loading.set(false);
    }
  }

  async saveProfile(): Promise<void> {
    if (this.savingProfile()) {
      return;
    }
    this.savingProfile.set(true);
    try {
      await firstValueFrom(
        this.aiProviders.updateProfile({ displayName: this.displayName().trim() }),
      );
      this.toast.success('Display name saved.');
    } catch {
      this.toast.error('Could not save your display name.');
    } finally {
      this.savingProfile.set(false);
    }
  }

  async savePassword(): Promise<void> {
    if (this.savingPassword()) {
      return;
    }

    this.passwordError.set('');

    // Catch the mismatch here rather than sending a request that can only fail.
    if (this.newPassword() !== this.confirmPassword()) {
      this.passwordError.set('The new passwords do not match.');
      return;
    }

    this.savingPassword.set(true);
    try {
      await firstValueFrom(
        this.aiProviders.changePassword({
          currentPassword: this.currentPassword(),
          newPassword: this.newPassword(),
        }),
      );
      this.currentPassword.set('');
      this.newPassword.set('');
      this.confirmPassword.set('');
      this.toast.success('Password changed.');
    } catch {
      this.passwordError.set(
        'Could not change your password. Check your current password and try again.',
      );
    } finally {
      this.savingPassword.set(false);
    }
  }

  defaultProviderName(): string {
    return this.providers().find((p) => p.isDefault)?.displayName ?? 'the system default';
  }

  async save(): Promise<void> {
    if (this.saving()) {
      return;
    }
    this.saving.set(true);
    const key = this.selectedProviderKey();

    try {
      await firstValueFrom(
        this.aiProviders.savePreferences({
          preferredAnalysisProviderKey: key === USE_DEFAULT ? null : key,
        }),
      );
      this.toast.success('Preference saved.');
    } catch {
      this.toast.error('Could not save your preference.');
    } finally {
      this.saving.set(false);
    }
  }

}
