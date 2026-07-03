import { createFeature, createReducer, on } from '@ngrx/store';
import { initialState } from '../state/call';
import * as callActions from '../actions/call.actions';

export const callFeature = createFeature({
  name: 'call',

  reducer: createReducer(
    initialState,

    on(callActions.updateCallState, (state, { isCallStarted }) => ({
      ...state,
      isCallStarted,
    })),
  ),
});
