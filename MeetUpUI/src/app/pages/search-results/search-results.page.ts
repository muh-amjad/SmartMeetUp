import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { SearchMode, SearchResultDto } from '../../dtos/meetings/search-result.dto';
import { SearchService } from '../../services/search.service';
import { ToastService } from '../../services/toast.service';

/** Results for one meeting, so a meeting with several matching moments reads as one entry. */
interface MeetingGroup {
  meetingId: string;
  meetingTitle: string;
  meetingDate: string;
  hits: SearchResultDto[];
}

@Component({
  selector: 'app-search-results',
  standalone: true,
  imports: [DatePipe, FormsModule],
  templateUrl: './search-results.page.html',
  styleUrl: './search-results.page.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SearchResultsPage implements OnInit {
  private readonly searchService = inject(SearchService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);

  readonly modes: ReadonlyArray<{ id: SearchMode; label: string; hint: string }> = [
    { id: 'hybrid', label: 'Hybrid', hint: 'Keyword and meaning combined' },
    { id: 'keyword', label: 'Keyword', hint: 'Exact words only' },
    { id: 'semantic', label: 'Meaning', hint: 'Related ideas, not just words' },
  ];

  readonly query = signal('');
  readonly mode = signal<SearchMode>('hybrid');
  readonly results = signal<SearchResultDto[]>([]);
  readonly searching = signal(false);
  readonly hasSearched = signal(false);

  /** Results arrive ranked; grouping preserves that order by first appearance. */
  readonly groups = computed<MeetingGroup[]>(() => {
    const grouped = new Map<string, MeetingGroup>();

    for (const hit of this.results()) {
      const existing = grouped.get(hit.meetingId);
      if (existing) {
        existing.hits.push(hit);
      } else {
        grouped.set(hit.meetingId, {
          meetingId: hit.meetingId,
          meetingTitle: hit.meetingTitle,
          meetingDate: hit.meetingDate,
          hits: [hit],
        });
      }
    }

    return [...grouped.values()];
  });

  ngOnInit(): void {
    // Deep links like /search?q=pricing run immediately.
    this.route.queryParamMap.subscribe((params) => {
      const q = params.get('q') ?? '';
      const mode = (params.get('mode') as SearchMode | null) ?? 'hybrid';

      this.query.set(q);
      this.mode.set(mode);

      if (q.trim()) {
        void this.runSearch();
      }
    });
  }

  /** Pushes the query into the URL so results are shareable and back/forward works. */
  submit(): void {
    const q = this.query().trim();
    if (!q) {
      return;
    }

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q, mode: this.mode() },
      replaceUrl: true,
    });
  }

  selectMode(mode: SearchMode): void {
    this.mode.set(mode);
    if (this.query().trim()) {
      this.submit();
    }
  }

  private async runSearch(): Promise<void> {
    this.searching.set(true);
    try {
      this.results.set(await firstValueFrom(this.searchService.search(this.query(), this.mode())));
    } catch {
      this.results.set([]);
      this.toast.error('Search failed. Please try again.');
    } finally {
      this.searching.set(false);
      this.hasSearched.set(true);
    }
  }

  formatTimestamp(startMs: number): string {
    const total = Math.floor(startMs / 1000);
    const hours = Math.floor(total / 3600);
    const minutes = Math.floor((total % 3600) / 60);
    const seconds = total % 60;
    const pad = (n: number) => n.toString().padStart(2, '0');
    return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${pad(minutes)}:${pad(seconds)}`;
  }

  /** Opens the meeting's transcript positioned at this moment. */
  jumpToMoment(hit: SearchResultDto): void {
    this.router.navigate(['/meetings', hit.meetingId], {
      queryParams: { seek: hit.startMs, tab: 'transcript' },
    });
  }

}
