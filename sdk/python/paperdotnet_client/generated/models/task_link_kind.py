from enum import Enum

class TaskLinkKind(str, Enum):
    Subtask = "subtask",
    BlockedBy = "blockedBy",
    Document = "document",

