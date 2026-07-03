import { Injectable, inject } from '@angular/core';
import { Store } from '@ngrx/store';
import * as callActions from '../actions/call.actions';
import { callFeature } from '../reducers/call.reducer';

@Injectable({ providedIn: 'root' })
export class CallFacade {
  private store = inject(Store);

  isCallStarted = this.store.selectSignal(callFeature.selectCallState);

  updateCallState(isCallStarted: boolean) {
    this.store.dispatch(callActions.updateCallState({ isCallStarted }));
  }
}
