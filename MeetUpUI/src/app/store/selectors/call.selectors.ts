import { createSelector } from '@ngrx/store';
import { callFeature } from '../reducers/call.reducer';

export const selectCallState = callFeature.selectCallState;

export const selectIsCallStarted = createSelector(
  selectCallState,
  (state) => state.isCallStarted
);
