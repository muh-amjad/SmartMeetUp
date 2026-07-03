import { inject, Injectable } from '@angular/core';
import { Actions, createEffect, ofType } from '@ngrx/effects';
import { tap } from 'rxjs/operators';

import * as UsersActions from '../actions/users.actions';
import { LoggingService } from '../../services/logging.service';

@Injectable()
export class UsersEffects {
  private actions$ = inject(Actions);
  private loggingService = inject(LoggingService);

  addUser$ = createEffect(
    () =>
      this.actions$.pipe(
        ofType(UsersActions.addUser),
        tap(({ user }) => {
          this.loggingService.info('[UsersEffects] ADD_USER triggered');

          this.loggingService.debug('[UsersEffects] User Data:', user);
        }),
      ),
    { dispatch: false },
  );

  removeUser$ = createEffect(
    () =>
      this.actions$.pipe(
        ofType(UsersActions.removeUser),
        tap(({ user }) => {
          this.loggingService.info('[UsersEffects] REMOVE_USER triggered');

          this.loggingService.debug('[UsersEffects] Removed User:', user);
        }),
      ),
    { dispatch: false },
  );
}
