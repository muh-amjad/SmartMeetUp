import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { SignalrService } from './services/signalr.service';
import { UsersEffects } from './store/effects/users';
import { usersFeature } from './store/reducers/users.reducer';
import { provideEffects } from '@ngrx/effects';
import { provideState, provideStore } from '@ngrx/store';
import { LoggingService } from './services/logging.service';
import { UsersFacade } from './store/facades/users.facade';
import { CallFacade } from './store/facades/call.facade';
import { callFeature } from './store/reducers/call.reducer';
import { CallEffects } from './store/effects/call';
import { authInterceptor } from './interceptors/auth.interceptor';

const features = [provideState(usersFeature), provideState(callFeature)];

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideStore(),
    ...features,
    provideEffects([UsersEffects, CallEffects]),
    SignalrService,
    LoggingService,
    UsersFacade,
    CallFacade,
  ],
};
