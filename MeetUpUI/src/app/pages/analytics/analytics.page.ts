import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AccountAnalyticsDto, WeeklyBucketDto } from '../../dtos/meetings/analysis.dto';
import { AiProviderService } from '../../services/ai-provider.service';
import { ToastService } from '../../services/toast.service';

@Component({
  selector: 'app-analytics',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  templateUrl: './analytics.page.html',
  styleUrl: './analytics.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AnalyticsPage implements OnInit {
  private readonly aiProviders = inject(AiProviderService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly analytics = signal<AccountAnalyticsDto | null>(null);
  readonly loading = signal(true);

  /** Tallest column defines the scale, so the busiest week always fills the plot. */
  private readonly maxWeeklyMeetings = computed(() =>
    Math.max(...(this.analytics()?.weeklyBreakdown ?? []).map((w) => w.meetingCount), 0),
  );

  readonly busiestWeek = computed<WeeklyBucketDto | null>(() => {
    const weeks = this.analytics()?.weeklyBreakdown ?? [];
    return weeks.length ? weeks.reduce((a, b) => (b.meetingCount > a.meetingCount ? b : a)) : null;
  });

  async ngOnInit(): Promise<void> {
    this.loading.set(true);
    try {
      this.analytics.set(await firstValueFrom(this.aiProviders.getAccountAnalytics()));
    } catch {
      this.toast.error('Could not load analytics.');
    } finally {
      this.loading.set(false);
    }
  }

  columnHeightPercent(week: WeeklyBucketDto): number {
    const max = this.maxWeeklyMeetings();
    return max > 0 ? Math.round((week.meetingCount / max) * 100) : 0;
  }

  /** Only the busiest column gets a direct label; the rest are covered by hover and the table. */
  isBusiest(week: WeeklyBucketDto): boolean {
    return this.busiestWeek()?.weekStartUtc === week.weekStartUtc;
  }

  /** Thin out the axis: a label on every week collides at this column width. */
  showTick(index: number): boolean {
    const total = this.analytics()?.weeklyBreakdown.length ?? 0;
    return index % 3 === 0 || index === total - 1;
  }

  formatSpeakingTime(totalSeconds: number): string {
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.round((totalSeconds % 3600) / 60);
    return hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
  }

  back(): void {
    this.router.navigate(['/dashboard']);
  }
}
