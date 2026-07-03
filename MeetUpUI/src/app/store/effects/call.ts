import { inject, Injectable } from '@angular/core';
import { Actions, createEffect, ofType } from '@ngrx/effects';
import { tap } from 'rxjs/operators';
import * as callActions from '../actions/call.actions';
import { LoggingService } from '../../services/logging.service';

@Injectable()
export class CallEffects {
  private actions$ = inject(Actions);
  private loggingService = inject(LoggingService);

  updateCallState$ = createEffect(
    () =>
      this.actions$.pipe(
        ofType(callActions.updateCallState),
        tap(({ isCallStarted }) => {
          this.loggingService.info(
            `[CallEffects] UPDATE_CALL_STATE triggered with isCallStarted: ${isCallStarted}`,
          );

          this.loggingService.debug('[CallEffects] Call State Data:', isCallStarted);
        }),
      ),
    { dispatch: false },
  );
}
