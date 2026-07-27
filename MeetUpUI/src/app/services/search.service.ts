import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { SearchMode, SearchResultDto } from '../dtos/meetings/search-result.dto';

@Injectable({ providedIn: 'root' })
export class SearchService {
  private readonly http = inject(HttpClient);

  /** Searches the caller's own transcripts; the server scopes results to their meetings. */
  search(query: string, mode: SearchMode = 'hybrid', limit = 20): Observable<SearchResultDto[]> {
    const params = new HttpParams().set('q', query).set('mode', mode).set('limit', limit);
    return this.http.get<SearchResultDto[]>(`${environment.apiBaseUrl}/api/search`, { params });
  }
}
