from enum import Enum

class RunStatus(str, Enum):
    Running = "running",
    Waiting = "waiting",
    Completed = "completed",
    Failed = "failed",
    Cancelled = "cancelled",
    Skipped = "skipped",

