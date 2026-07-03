import { createSelector } from '@ngrx/store';
import { usersFeature } from '../reducers/users.reducer';

export const selectUsersState = usersFeature.selectUsersState;

export const selectUsers = createSelector(selectUsersState, (state) => state.users);

export const selectLoading = createSelector(selectUsersState, (state) => state.loading);
