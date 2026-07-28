import { Routes } from '@angular/router';
import { AppShellComponent } from './components/app-shell/app-shell.component';
import { authGuard } from './guards/auth.guard';
import { ActionItemsPage } from './pages/action-items/action-items.page';
import { AnalyticsPage } from './pages/analytics/analytics.page';
import { DashboardPage } from './pages/dashboard/dashboard.page';
import { HomePage } from './pages/home/home';
import { LoginPage } from './pages/login/login';
import { MeetingDetailPage } from './pages/meeting-detail/meeting-detail.page';
import { MeetingHistoryPage } from './pages/meeting-history/meeting-history.page';
import { MeetingPreviewPage } from './pages/meeting-preview/meeting-preview';
import { MeetupHome } from './pages/meetup-home/meetup-home';
import { SearchResultsPage } from './pages/search-results/search-results.page';
import { SettingsPage } from './pages/settings/settings.page';
import { SignupPage } from './pages/signup/signup';

export const routes: Routes = [
  // Public
  { path: '', component: HomePage },
  { path: 'login', component: LoginPage },
  { path: 'signup', component: SignupPage },

  // Signed-in app: sidebar + topbar wrap every page below.
  {
    path: '',
    component: AppShellComponent,
    canActivate: [authGuard],
    children: [
      { path: 'dashboard', component: DashboardPage },
      { path: 'meetings', component: MeetingHistoryPage },
      { path: 'meetings/:meetingId', component: MeetingDetailPage },
      { path: 'action-items', component: ActionItemsPage },
      { path: 'analytics', component: AnalyticsPage },
      { path: 'search', component: SearchResultsPage },
      { path: 'settings', component: SettingsPage },
    ],
  },

  // Deliberately outside the shell — a call and its device check use the whole window.
  { path: 'preview', component: MeetingPreviewPage, canActivate: [authGuard] },
  { path: 'meet/:meetingId', component: MeetupHome, canActivate: [authGuard] },
  { path: 'meet', component: MeetupHome, canActivate: [authGuard] },

  { path: '**', redirectTo: '' },
];
