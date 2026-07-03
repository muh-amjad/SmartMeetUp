import { createFeature, createReducer, on } from '@ngrx/store';
import { initialState } from '../state/users';
import * as UsersActions from '../actions/users.actions';
import { User } from '../../models/user.model';

export const usersFeature = createFeature({
  name: 'users',

  reducer: createReducer(
    initialState,

    on(UsersActions.addUser, (state, { user }) => ({
      ...state,
      users: [...state.users, user],
    })),

    on(UsersActions.removeUser, (state, { user }) => ({
      ...state,
      users: state.users.filter((u: User) => u.id !== user.id),
    })),

    on(UsersActions.setLoading, (state, { loading }) => ({
      ...state,
      loading,
    })),
    on(UsersActions.updateUserList, (state, { users }) => ({
      ...state,
      users,
    })),
  ),
});
