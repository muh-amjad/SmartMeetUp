import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { UserSearchResultDto } from '../dtos/user-search-result.dto';

const API_BASE_URL = 'https://localhost:7248/api';

@Injectable({ providedIn: 'root' })
export class UserDirectoryService {
  private readonly http = inject(HttpClient);

  searchUsers(query: string) {
    const params = new HttpParams().set('query', query);
    return this.http.get<UserSearchResultDto[]>(`${API_BASE_URL}/users/search`, { params });
  }
}
