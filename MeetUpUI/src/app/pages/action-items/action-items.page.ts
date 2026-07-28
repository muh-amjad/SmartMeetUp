import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ActionItemFilter, ActionItemWithMeetingDto } from '../../dtos/meetings/analysis.dto';
import { AiProviderService } from '../../services/ai-provider.service';
import { MeetingApiService } from '../../services/meeting-api.service';
import { ToastService } from '../../services/toast.service';

/** Items grouped under the meeting they came from. */
interface MeetingGroup {
  meetingId: string;
  meetingTitle: string;
  meetingDate: string;
  items: ActionItemWithMeetingDto[];
}

@Component({
  selector: 'app-action-items',
  standalone: true,
  imports: [DatePipe, RouterLink],
  templateUrl: './action-items.page.html',
  styleUrl: './action-items.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ActionItemsPage implements OnInit {
  private readonly aiProviders = inject(AiProviderService);
  private readonly meetingApi = inject(MeetingApiService);
  private readonly toast = inject(ToastService);

  readonly filters: ReadonlyArray<{ id: ActionItemFilter; label: string }> = [
    { id: 'open', label: 'Open' },
    { id: 'overdue', label: 'Overdue' },
    { id: 'mine', label: 'Assigned to me' },
    { id: 'done', label: 'Done' },
    { id: 'all', label: 'All' },
  ];

  readonly filter = signal<ActionItemFilter>('open');
  readonly items = signal<ActionItemWithMeetingDto[]>([]);
  readonly loading = signal(true);

  /** Server returns items already ordered; grouping keeps that order per meeting. */
  readonly groups = computed<MeetingGroup[]>(() => {
    const grouped = new Map<string, MeetingGroup>();

    for (const item of this.items()) {
      const existing = grouped.get(item.meetingId);
      if (existing) {
        existing.items.push(item);
      } else {
        grouped.set(item.meetingId, {
          meetingId: item.meetingId,
          meetingTitle: item.meetingTitle,
          meetingDate: item.meetingDate,
          items: [item],
        });
      }
    }

    return [...grouped.values()];
  });

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  async selectFilter(filter: ActionItemFilter): Promise<void> {
    this.filter.set(filter);
    await this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.items.set(await firstValueFrom(this.aiProviders.getActionItems(this.filter())));
    } catch {
      this.items.set([]);
      this.toast.error('Could not load action items.');
    } finally {
      this.loading.set(false);
    }
  }

  async toggle(item: ActionItemWithMeetingDto): Promise<void> {
    const nextStatus = item.status === 'Done' ? 'Open' : 'Done';
    try {
      await firstValueFrom(this.meetingApi.updateActionItem(item.id, { status: nextStatus }));

      // The active filter may no longer include this item, so reload rather than patch in place.
      await this.load();
    } catch {
      this.toast.error('Could not update the action item.');
    }
  }

  assigneeLabel(item: ActionItemWithMeetingDto): string {
    return item.assigneeUsername ?? item.assigneeNameRaw ?? 'Unassigned';
  }
}
