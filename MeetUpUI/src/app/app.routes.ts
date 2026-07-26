import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';
import { HomePage } from './pages/home/home';
import { LoginPage } from './pages/login/login';
import { MeetingPreviewPage } from './pages/meeting-preview/meeting-preview';
import { MeetupHome } from './pages/meetup-home/meetup-home';
import { SignupPage } from './pages/signup/signup';
import { MeetingHistoryPage } from './pages/meeting-history/meeting-history.page';
import { MeetingDetailPage } from './pages/meeting-detail/meeting-detail.page';
import { SettingsPage } from './pages/settings/settings.page';

export const routes: Routes = [
  { path: '', component: HomePage },
  { path: 'login', component: LoginPage },
  { path: 'signup', component: SignupPage },
  {
    path: 'dashboard',
    component: MeetupHome,
    canActivate: [authGuard],
    data: { mode: 'dashboard' },
  },
  { path: 'preview', component: MeetingPreviewPage, canActivate: [authGuard] },
      { path: 'meetings', component: MeetingHistoryPage, canActivate: [authGuard] },
  { path: 'meetings/:meetingId', component: MeetingDetailPage, canActivate: [authGuard] },
  { path: 'settings', component: SettingsPage, canActivate: [authGuard] },
  { path: 'meet/:meetingId', component: MeetupHome, canActivate: [authGuard],data: { mode: 'call' } },
  { path: 'meet', component: MeetupHome, canActivate: [authGuard], data: { mode: 'call' } },
  { path: '**', redirectTo: '' },
];
