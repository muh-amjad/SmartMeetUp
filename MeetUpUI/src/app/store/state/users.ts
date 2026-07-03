import { User } from '../../models/user.model';

export interface UsersState {
  users: User[];
  loading: boolean;
}

export const initialState: UsersState = {
  users: [],
  loading: false
};
