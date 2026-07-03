import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';
import { HomePage } from './pages/home/home';
import { LoginPage } from './pages/login/login';
import { MeetingPreviewPage } from './pages/meeting-preview/meeting-preview';
import { MeetupHome } from './pages/meetup-home/meetup-home';
import { SignupPage } from './pages/signup/signup';

export const routes: Routes = [
    { path: '', component: HomePage },
    { path: 'login', component: LoginPage },
    { path: 'signup', component: SignupPage },
    { path: 'dashboard', component: MeetupHome, canActivate: [authGuard], data: { mode: 'dashboard' } },
    { path: 'preview', component: MeetingPreviewPage, canActivate: [authGuard] },
    { path: 'meet', component: MeetupHome, canActivate: [authGuard] },
    { path: '**', redirectTo: '' },
];
