export interface MeetingSummaryDto {
  overviewText: string;
  keyTopics: string[];
  providerKey: string;
  providerDisplayName: string;
  modelUsed: string;
  generatedUtc: string;
}

export type ActionItemStatus = 'Open' | 'Done' | 'Cancelled';

export interface ActionItemDto {
  id: string;
  meetingId: string;
  description: string;
  assigneeUserId: string | null;
  assigneeUsername: string | null;
  assigneeNameRaw: string | null;
  dueDateUtc: string | null;
  status: ActionItemStatus;
  completedUtc: string | null;
  sourceStartMs: number | null;
}

export interface UpdateActionItemRequestDto {
  description?: string;
  status?: ActionItemStatus;
  assigneeUserId?: string;
  dueDateUtc?: string | null;
  clearDueDate?: boolean;
}

export interface DecisionDto {
  id: string;
  description: string;
  sourceStartMs: number | null;
  createdUtc: string;
}

export interface FollowUpEmailDto {
  subject: string;
  bodyMarkdown: string;
  status: string;
  sentUtc: string | null;
  createdUtc: string;
}

export interface UpdateFollowUpEmailRequestDto {
  subject: string;
  bodyMarkdown: string;
}

export interface AnalysisProviderDto {
  key: string;
  displayName: string;
  isFree: boolean;
  isDefault: boolean;
  contextWindow: number;
}

export interface UserPreferencesDto {
  preferredAnalysisProviderKey: string | null;
}
