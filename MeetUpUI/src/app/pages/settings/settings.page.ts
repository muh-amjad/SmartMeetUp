import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
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
  private readonly router = inject(Router);

  readonly useDefault = USE_DEFAULT;

  readonly providers = signal<AnalysisProviderDto[]>([]);
  readonly selectedProviderKey = signal<string>(USE_DEFAULT);
  readonly loading = signal(true);
  readonly saving = signal(false);

  async ngOnInit(): Promise<void> {
    this.loading.set(true);
    try {
      const [providers, preferences] = await Promise.all([
        firstValueFrom(this.aiProviders.getProviders()),
        firstValueFrom(this.aiProviders.getPreferences()),
      ]);
      this.providers.set(providers);
      this.selectedProviderKey.set(preferences.preferredAnalysisProviderKey ?? USE_DEFAULT);
    } catch {
      this.toast.error('Could not load settings.');
    } finally {
      this.loading.set(false);
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

  back(): void {
    this.router.navigate(['/dashboard']);
  }
}
